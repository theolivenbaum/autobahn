using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Autobahn.Tests;

/// <summary>
/// A tiny HTTP and WebSocket server the protocol tests run against.
/// </summary>
/// <remarks>
/// A real server on a real socket, rather than a stubbed handler: the point of these tests is
/// the wire - status codes, header bytes, chunked bodies, a socket that closes mid-wait - and
/// a fake <c>HttpMessageHandler</c> tests only the code that would have called one.
/// <see cref="HttpListener"/> is in the framework, needs nothing installed and is more than
/// enough for a handful of routes.
/// </remarks>
public sealed class TestServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private TestServer(int port)
    {
        Port = port;
        BaseAddress = $"http://127.0.0.1:{port}/";

        _listener.Prefixes.Add(BaseAddress);
        _listener.Start();

        _loop = Task.Run(Accept);
    }

    public int Port { get; }
    public string BaseAddress { get; }

    /// <summary>How many requests the server has answered.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    private int _requestCount;

    /// <summary>
    /// Starts a server on a free port.
    /// </summary>
    /// <remarks>
    /// The port is picked by binding a socket to 0 and reading back what the OS chose, then
    /// releasing it. There is a race with anything else on the machine grabbing it in between,
    /// which is why a failed bind is retried rather than failing the test.
    /// </remarks>
    public static TestServer Start()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var port = FreePort();

            try
            {
                return new TestServer(port);
            }
            catch (HttpListenerException)
            {
                // Something took the port between us asking for one and binding it.
            }
        }

        throw new InvalidOperationException("Could not find a free port for the test server.");
    }

    private static int FreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    private async Task Accept()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            Interlocked.Increment(ref _requestCount);
            _ = Task.Run(() => Handle(context));
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";

            if (path == "/ws" && context.Request.IsWebSocketRequest)
            {
                await HandleWebSocket(context).ConfigureAwait(false);
                return;
            }

            await HandleHttp(context, path).ConfigureAwait(false);
        }
        catch
        {
            // A client that hung up mid-response is the test's business, not the server's.
        }
    }

    private static async Task HandleHttp(HttpListenerContext context, string path)
    {
        var response = context.Response;
        var body = "";

        switch (path)
        {
            case "/ok":
                response.StatusCode = 200;
                response.ContentType = "application/json";
                body = """{"status":"ok"}""";
                break;

            case "/created":
                response.StatusCode = 201;
                body = "created";
                break;

            case "/error":
                response.StatusCode = 500;
                body = "boom";
                break;

            case "/notfound":
                response.StatusCode = 404;
                body = "nope";
                break;

            // 200 with an error in the body: the case a status-only check gets wrong.
            case "/lying":
                response.StatusCode = 200;
                response.ContentType = "application/json";
                body = """{"error":"not really ok"}""";
                break;

            case "/slow":
                await Task.Delay(2_000).ConfigureAwait(false);
                response.StatusCode = 200;
                body = "eventually";
                break;

            case "/echo":
                using (var reader = new StreamReader(context.Request.InputStream))
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);

                response.StatusCode = 200;
                response.ContentType = context.Request.ContentType ?? "text/plain";
                break;

            case "/headers":
                response.StatusCode = 200;
                response.ContentType = "text/plain";
                body = string.Join("\n", context.Request.Headers.AllKeys
                    .Where(k => k is not null)
                    .Select(k => $"{k}: {context.Request.Headers[k]}"));
                break;

            case "/big":
                response.StatusCode = 200;
                response.ContentType = "text/plain";
                body = new string('x', 10_000);
                break;

            default:
                response.StatusCode = 404;
                body = "unknown route";
                break;
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;

        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    /// <summary>Echoes text frames, prefixed so a response is tellable from an unsolicited push.</summary>
    private static async Task HandleWebSocket(HttpListenerContext context)
    {
        var ws = (await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false)).WebSocket;
        var buffer = new byte[64 * 1024];

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);

            if (text == "close")
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "asked", CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            // An unsolicited message first, so a test's correlation predicate has something to
            // skip past on its way to the answer.
            await Send(ws, "push:heartbeat").ConfigureAwait(false);
            await Send(ws, $"echo:{text}").ConfigureAwait(false);
        }

        static Task Send(WebSocket ws, string message) =>
            ws.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        _listener.Stop();
        _listener.Close();

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch
        {
            // Stopping the listener is how the loop ends; whatever it threw on the way out is
            // not a test failure.
        }

        _cts.Dispose();
    }
}
