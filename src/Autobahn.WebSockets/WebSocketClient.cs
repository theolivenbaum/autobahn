using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;

namespace Autobahn.WebSockets;

/// <summary>
/// One virtual user's WebSocket connection, with the two shapes a load test needs from one.
/// </summary>
/// <remarks>
/// A WebSocket is not request/response, and pretending it is quietly measures the wrong
/// thing. Two patterns cover almost everything a test wants:
///
/// - **Request/response**: send, then wait for the answer that belongs to what was sent. The
///   caller says which message that is, because only the protocol on top knows - correlation
///   ids, subjects, sequence numbers, all of it is above this layer.
/// - **Publish then consume**: one scenario publishes and another consumes, and what is being
///   measured is the delivery latency between them rather than either side's own speed.
///
/// The connection is not thread-safe for concurrent sends, which is the underlying socket's
/// rule rather than this class's; a send lock is held so two steps in one iteration cannot
/// interleave frames, but two scenario copies must not share one of these.
/// </remarks>
public sealed class WebSocketClient : IAsyncDisposable, IDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly int _receiveBufferSize;

    private WebSocketClient(int receiveBufferSize) => _receiveBufferSize = receiveBufferSize;

    /// <summary>The socket underneath, for anything this class does not cover.</summary>
    public ClientWebSocket Socket => _socket;

    public WebSocketState State => _socket.State;

    /// <summary>Opens a connection and hands back the client that owns it.</summary>
    public static async Task<WebSocketClient> ConnectAsync(
        string url,
        Action<ClientWebSocket>? configure = null,
        int receiveBufferSize = 64 * 1024,
        CancellationToken cancellationToken = default)
    {
        var client = new WebSocketClient(receiveBufferSize);

        try
        {
            configure?.Invoke(client._socket);
            await client._socket.ConnectAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A pool of open connections, one per virtual user, handed out by copy index.
    /// </summary>
    /// <remarks>
    /// A pool rather than one shared connection because a WebSocket is a session: N virtual
    /// users on one socket are one user sending N times as much, which is a different test.
    /// </remarks>
    public static async Task<ClientPool<WebSocketClient>> CreatePoolAsync(
        string url,
        int count,
        Action<ClientWebSocket>? configure = null,
        CancellationToken cancellationToken = default)
    {
        if (count < 1)
            throw new AutobahnException($"A WebSocket pool of {count} connections is not something a scenario can use.");

        var pool = new ClientPool<WebSocketClient>();

        for (var i = 0; i < count; i++)
        {
            pool.AddClient(await ConnectAsync(url, configure, cancellationToken: cancellationToken).ConfigureAwait(false));
        }

        return pool;
    }

    /// <summary>Sends a text frame and records how many bytes went out.</summary>
    public async Task<Response<object>> SendText(string message, IScenarioContext context)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        return await Send(bytes, WebSocketMessageType.Text, context).ConfigureAwait(false);
    }

    /// <summary>Sends a binary frame and records how many bytes went out.</summary>
    public async Task<Response<object>> SendBinary(byte[] payload, IScenarioContext context) =>
        await Send(payload, WebSocketMessageType.Binary, context).ConfigureAwait(false);

    private async Task<Response<object>> Send(
        byte[] payload, WebSocketMessageType type, IScenarioContext context)
    {
        var started = Stopwatch.GetTimestamp();

        await _sendLock.WaitAsync(context.CancellationToken).ConfigureAwait(false);

        try
        {
            await _socket
                .SendAsync(payload, type, endOfMessage: true, context.CancellationToken)
                .ConfigureAwait(false);

            return Response.Ok(sizeBytes: payload.Length, latencyMs: Elapsed(started));
        }
        catch (WebSocketException ex)
        {
            return Failure(ex.Message, started);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Waits for the next whole message, however many frames it arrives in.
    /// </summary>
    public async Task<Response<WebSocketMessage>> Receive(IScenarioContext context)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var message = await ReceiveMessage(context.CancellationToken).ConfigureAwait(false);

            return message is null
                ? Response.FailOf<WebSocketMessage>() with
                {
                    StatusCode = WebSocketStatusCodes.Closed,
                    Message = "the connection closed while waiting for a message",
                    LatencyMs = Elapsed(started)
                }
                : Response.Ok(message, sizeBytes: message.SizeBytes, latencyMs: Elapsed(started));
        }
        catch (WebSocketException ex)
        {
            return Response.FailOf<WebSocketMessage>() with
            {
                StatusCode = WebSocketStatusCodes.TransportError,
                Message = ex.Message,
                LatencyMs = Elapsed(started)
            };
        }
    }

    /// <summary>
    /// Sends a message and waits for the answer that belongs to it.
    /// </summary>
    /// <param name="request">What to send.</param>
    /// <param name="isResponse">
    /// Which incoming message is the answer. Only the protocol on top of the socket knows -
    /// a correlation id, a subject, a sequence number - so the caller says.
    /// </param>
    /// <param name="context">The iteration asking.</param>
    /// <param name="timeout">How long to wait before giving up. Null waits as long as the iteration does.</param>
    /// <remarks>
    /// Messages that arrive while waiting and are not the answer are dropped, not queued: this
    /// shape is for a socket where the test owns the conversation. A test where unrelated
    /// traffic shares the connection wants the publish-then-consume shape instead, with a
    /// consumer scenario of its own.
    /// </remarks>
    public async Task<Response<WebSocketMessage>> SendAndReceive(
        string request,
        Func<WebSocketMessage, bool> isResponse,
        IScenarioContext context,
        TimeSpan? timeout = null)
    {
        var started = Stopwatch.GetTimestamp();

        using var timeoutCts = timeout is { } limit
            ? CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken)
            : null;

        timeoutCts?.CancelAfter(timeout!.Value);
        var token = timeoutCts?.Token ?? context.CancellationToken;

        var requestBytes = Encoding.UTF8.GetBytes(request);

        try
        {
            await _sendLock.WaitAsync(token).ConfigureAwait(false);

            try
            {
                await _socket
                    .SendAsync(requestBytes, WebSocketMessageType.Text, endOfMessage: true, token)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            while (true)
            {
                var message = await ReceiveMessage(token).ConfigureAwait(false);

                if (message is null)
                {
                    return Response.FailOf<WebSocketMessage>() with
                    {
                        StatusCode = WebSocketStatusCodes.Closed,
                        Message = "the connection closed while waiting for a response",
                        SizeBytes = requestBytes.Length,
                        LatencyMs = Elapsed(started)
                    };
                }

                if (!isResponse(message)) continue;

                return Response.Ok(
                    message,
                    sizeBytes: requestBytes.Length + message.SizeBytes,
                    latencyMs: Elapsed(started));
            }
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true
                                                 && !context.CancellationToken.IsCancellationRequested)
        {
            return Response.FailOf<WebSocketMessage>() with
            {
                StatusCode = WebSocketStatusCodes.ResponseTimeout,
                Message = $"no response within {timeout}",
                SizeBytes = requestBytes.Length,
                LatencyMs = Elapsed(started)
            };
        }
        catch (WebSocketException ex)
        {
            return Response.FailOf<WebSocketMessage>() with
            {
                StatusCode = WebSocketStatusCodes.TransportError,
                Message = ex.Message,
                SizeBytes = requestBytes.Length,
                LatencyMs = Elapsed(started)
            };
        }
    }

    /// <summary>Reads frames until one message is complete, or null when the peer closed.</summary>
    private async Task<WebSocketMessage?> ReceiveMessage(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_receiveBufferSize);

        try
        {
            using var assembled = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close) return null;

                assembled.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var payload = assembled.ToArray();

            return new WebSocketMessage
            {
                MessageType = result.MessageType,
                Payload = payload,
                SizeBytes = payload.Length
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Response<object> Failure(string message, long started) =>
        Response.Fail() with
        {
            StatusCode = WebSocketStatusCodes.TransportError,
            Message = message,
            LatencyMs = Elapsed(started)
        };

    private static double Elapsed(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                await _socket
                    .CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // A connection that will not close politely is closed impolitely below. A load
            // test's teardown is not the place to fail over a goodbye.
        }

        Dispose();
    }

    public void Dispose()
    {
        _socket.Dispose();
        _sendLock.Dispose();
    }
}

/// <summary>One whole message off a socket.</summary>
public sealed record WebSocketMessage
{
    public required System.Net.WebSockets.WebSocketMessageType MessageType { get; init; }
    public required byte[] Payload { get; init; }
    public required long SizeBytes { get; init; }

    /// <summary>The payload as UTF-8 text.</summary>
    public string Text => Encoding.UTF8.GetString(Payload);
}

/// <summary>The status codes Autobahn reports for a socket that did not answer normally.</summary>
public static class WebSocketStatusCodes
{
    /// <summary>The peer closed the connection while the iteration was waiting.</summary>
    public const string Closed = "-210";

    /// <summary>The socket itself failed: connect, handshake, reset.</summary>
    public const string TransportError = "-211";

    /// <summary>Nothing that looked like the answer arrived within the timeout.</summary>
    public const string ResponseTimeout = "-212";
}
