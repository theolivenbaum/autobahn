using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Protocol;

namespace Autobahn.Mqtt;

/// <summary>
/// One virtual user's MQTT connection, with the two shapes a load test needs from a broker.
/// </summary>
/// <remarks>
/// MQTT is not request/response, and a helper that pretends it is measures the wrong thing.
/// Two patterns cover almost everything a test wants, and they are the same two the WebSocket
/// helper offers because they are properties of messaging rather than of a transport:
///
/// - **Request/response over topics**: publish, then wait for the message that answers it. The
///   caller says which one that is, because only the protocol on top knows - a correlation id,
///   a reply topic, a sequence number.
/// - **Publish then consume**: one scenario publishes and another consumes, and what is being
///   measured is the delivery latency *between* them rather than either side's own speed.
///   <see cref="PublishStamped"/> and <see cref="ReceiveStamped"/> are that pair.
///
/// Named a connection rather than a client because MQTTnet already has an <c>MqttClient</c>,
/// and a scenario file that says <c>using MQTTnet;</c> would otherwise have to disambiguate
/// every mention of it.
///
/// One of these per virtual user. Sharing one across scenario copies is a different test: N
/// users on one connection are one user publishing N times as much, and the broker sees one
/// session rather than N.
/// </remarks>
public sealed class MqttConnection : IAsyncDisposable, IDisposable
{
    private readonly IMqttClient _client;
    private readonly Channel<MqttMessage> _inbox;

    private long _dropped;

    private MqttConnection(IMqttClient client, int inboxCapacity)
    {
        _client = client;

        // Bounded, and dropping the oldest. An unbounded inbox in a load test is a memory leak
        // waiting for a slow consumer; dropping says the consumer could not keep up, which is a
        // finding rather than an error, and Dropped is where it is said.
        _inbox = Channel.CreateBounded<MqttMessage>(
            new BoundedChannelOptions(inboxCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            },
            _ => Interlocked.Increment(ref _dropped));

        _client.ApplicationMessageReceivedAsync += OnMessage;
    }

    /// <summary>The MQTTnet client underneath, for anything this class does not cover.</summary>
    public IMqttClient Client => _client;

    public bool IsConnected => _client.IsConnected;

    /// <summary>
    /// How many delivered messages this connection had to throw away.
    /// </summary>
    /// <remarks>
    /// Not zero means the consumer scenario is slower than the broker is delivering, which
    /// makes every latency it reports optimistic. Worth putting on a gauge.
    /// </remarks>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>How many delivered messages are waiting to be read.</summary>
    public int Waiting => _inbox.Reader.Count;

    /// <summary>Opens a connection and hands back the object that owns it.</summary>
    public static async Task<MqttConnection> ConnectAsync(
        string host,
        int port = 1883,
        string? clientId = null,
        Action<MqttClientOptionsBuilder>? configure = null,
        int inboxCapacity = 1024,
        CancellationToken cancellationToken = default)
    {
        var options = new MqttClientOptionsBuilder().WithTcpServer(host, port);

        // A client id the broker has never seen, unless the caller wants a specific one. Two
        // connections sharing an id is a protocol-level fight: the broker disconnects the first
        // one, which looks like a flaky network rather than like a duplicate id.
        options.WithClientId(clientId ?? "autobahn-" + Guid.NewGuid().ToString("N"));

        configure?.Invoke(options);

        var connection = new MqttConnection(new MqttClientFactory().CreateMqttClient(), inboxCapacity);

        try
        {
            await connection._client.ConnectAsync(options.Build(), cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>A pool of open connections, one per virtual user, handed out by copy index.</summary>
    public static async Task<ClientPool<MqttConnection>> CreatePoolAsync(
        string host,
        int count,
        int port = 1883,
        Action<MqttClientOptionsBuilder>? configure = null,
        int inboxCapacity = 1024,
        CancellationToken cancellationToken = default)
    {
        if (count < 1)
            throw new AutobahnException($"An MQTT pool of {count} connections is not something a scenario can use.");

        var pool = new ClientPool<MqttConnection>();

        for (var i = 0; i < count; i++)
        {
            pool.AddClient(await ConnectAsync(
                host, port, clientId: null, configure, inboxCapacity, cancellationToken).ConfigureAwait(false));
        }

        return pool;
    }

    /// <summary>
    /// Subscribes to a topic filter. Everything delivered afterwards lands in the inbox.
    /// </summary>
    /// <remarks>
    /// The setup form, for <c>WithInit</c>: there is no scenario context there, and a
    /// subscription that could not be made is not a slow iteration to be recorded - it is a
    /// test that cannot run, so it throws.
    /// </remarks>
    public async Task SubscribeAsync(
        string topicFilter,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
        CancellationToken cancellationToken = default)
    {
        var options = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(filter => filter.WithTopic(topicFilter).WithQualityOfServiceLevel(qos))
            .Build();

        var result = await _client.SubscribeAsync(options, cancellationToken).ConfigureAwait(false);

        // A broker may accept the connection and refuse the subscription; a scenario that did
        // not look would then wait forever for a message that was never coming.
        var refused = result.Items.FirstOrDefault(x => x.ResultCode > MqttClientSubscribeResultCode.GrantedQoS2);

        if (refused is not null)
            throw new AutobahnException($"The broker answered {refused.ResultCode} for '{topicFilter}'.");
    }

    /// <summary>
    /// Subscribes as a measured step inside an iteration.
    /// </summary>
    /// <remarks>
    /// Rarely what a test wants - a subscription belongs in <c>WithInit</c>, and one made per
    /// iteration measures the broker's subscribe path rather than its delivery path - but a
    /// test whose subject *is* subscribing needs it.
    /// </remarks>
    public async Task<Response<object>> Subscribe(
        string topicFilter,
        IScenarioContext context,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            await SubscribeAsync(topicFilter, qos, context.CancellationToken).ConfigureAwait(false);

            return Response.Ok(latencyMs: Elapsed(started));
        }
        catch (AutobahnException ex)
        {
            return Response.Fail() with
            {
                StatusCode = MqttStatusCodes.SubscriptionRefused,
                Message = ex.Message,
                LatencyMs = Elapsed(started)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure(ex, started);
        }
    }

    /// <summary>Publishes a payload and reports how long the broker took to accept it.</summary>
    public async Task<Response<object>> Publish(
        string topic,
        byte[] payload,
        IScenarioContext context,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var result = await Send(topic, payload, qos, retain, context.CancellationToken).ConfigureAwait(false);

            return result.IsSuccess
                ? Response.Ok(sizeBytes: payload.Length, latencyMs: Elapsed(started))
                : Response.Fail() with
                {
                    StatusCode = MqttStatusCodes.NotAccepted,
                    Message = $"the broker answered {result.ReasonCode}",
                    SizeBytes = payload.Length,
                    LatencyMs = Elapsed(started)
                };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure(ex, started);
        }
    }

    /// <summary>Publishes UTF-8 text.</summary>
    public Task<Response<object>> Publish(
        string topic,
        string payload,
        IScenarioContext context,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false) =>
        Publish(topic, Encoding.UTF8.GetBytes(payload), context, qos, retain);

    /// <summary>
    /// Publishes a payload with the time it was sent written into it.
    /// </summary>
    /// <remarks>
    /// The publisher half of the publish-then-consume shape. What it measures is still the
    /// broker accepting the message - the delivery latency is measured by whoever receives it,
    /// which is the point of splitting the two.
    /// </remarks>
    public Task<Response<object>> PublishStamped(
        string topic,
        byte[] body,
        IScenarioContext context,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false) =>
        Publish(topic, MqttDelivery.Stamp(body), context, qos, retain);

    /// <summary>Publishes UTF-8 text with the time it was sent written into it.</summary>
    public Task<Response<object>> PublishStamped(
        string topic,
        string body,
        IScenarioContext context,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce,
        bool retain = false) =>
        PublishStamped(topic, Encoding.UTF8.GetBytes(body), context, qos, retain);

    /// <summary>
    /// Waits for the next delivered message.
    /// </summary>
    /// <remarks>
    /// The latency reported is how long this iteration waited, which is a property of the test
    /// rather than of the broker: a consumer with nothing to consume waits for as long as the
    /// publisher takes. Use <see cref="ReceiveStamped"/> when the number that matters is how
    /// long the message took to arrive.
    /// </remarks>
    public async Task<Response<MqttMessage>> Receive(IScenarioContext context, TimeSpan? timeout = null)
    {
        var started = Stopwatch.GetTimestamp();
        var message = await Next(context, timeout).ConfigureAwait(false);

        return message is null
            ? Timeout<MqttMessage>(timeout, started)
            : Response.Ok(message, sizeBytes: message.SizeBytes, latencyMs: Elapsed(started));
    }

    /// <summary>
    /// Waits for the next delivered message and reports how long it took to get here.
    /// </summary>
    /// <remarks>
    /// The consumer half of the publish-then-consume shape, and the only one of these methods
    /// whose latency is about the broker rather than about the test: it is the time between
    /// <see cref="PublishStamped"/> writing the stamp and this reading it.
    ///
    /// A message with no stamp is a failure rather than a zero. It means something other than
    /// this test is publishing to the topic, and silently reporting that as instant delivery
    /// would be the most flattering possible lie.
    /// </remarks>
    public async Task<Response<MqttMessage>> ReceiveStamped(IScenarioContext context, TimeSpan? timeout = null)
    {
        var started = Stopwatch.GetTimestamp();
        var message = await Next(context, timeout).ConfigureAwait(false);

        if (message is null) return Timeout<MqttMessage>(timeout, started);

        if (!MqttDelivery.TryRead(message.Payload, out var delay, out var body))
        {
            return Response.FailOf<MqttMessage>() with
            {
                StatusCode = MqttStatusCodes.Unstamped,
                Message = $"a message on '{message.Topic}' carried no delivery stamp",
                SizeBytes = message.SizeBytes,
                LatencyMs = Elapsed(started)
            };
        }

        return Response.Ok(
            message with { Payload = body, SizeBytes = body.Length },
            sizeBytes: body.Length,
            latencyMs: delay.TotalMilliseconds);
    }

    /// <summary>
    /// Publishes and waits for the message that answers it.
    /// </summary>
    /// <param name="isResponse">
    /// Which delivered message is the answer. Only the protocol above MQTT knows - a
    /// correlation id, a reply topic, a sequence number - so the caller says.
    /// </param>
    /// <remarks>
    /// Messages that arrive while waiting and are not the answer are dropped, not put back:
    /// this shape is for a connection whose conversation the test owns. A test where other
    /// traffic shares the subscription wants a consumer scenario of its own instead.
    /// </remarks>
    public async Task<Response<MqttMessage>> PublishAndReceive(
        string topic,
        byte[] payload,
        Func<MqttMessage, bool> isResponse,
        IScenarioContext context,
        TimeSpan? timeout = null,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var published = await Send(topic, payload, qos, retain: false, context.CancellationToken)
                .ConfigureAwait(false);

            if (!published.IsSuccess)
            {
                return Response.FailOf<MqttMessage>() with
                {
                    StatusCode = MqttStatusCodes.NotAccepted,
                    Message = $"the broker answered {published.ReasonCode}",
                    SizeBytes = payload.Length,
                    LatencyMs = Elapsed(started)
                };
            }

            while (true)
            {
                var message = await Next(context, Remaining(timeout, started)).ConfigureAwait(false);

                if (message is null)
                {
                    return Timeout<MqttMessage>(timeout, started) with { SizeBytes = payload.Length };
                }

                if (!isResponse(message)) continue;

                return Response.Ok(
                    message,
                    sizeBytes: payload.Length + message.SizeBytes,
                    latencyMs: Elapsed(started));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Response.FailOf<MqttMessage>() with
            {
                StatusCode = StatusCodeFor(ex),
                Message = ex.Message,
                SizeBytes = payload.Length,
                LatencyMs = Elapsed(started)
            };
        }
    }

    /// <summary>Publishes UTF-8 text and waits for the message that answers it.</summary>
    public Task<Response<MqttMessage>> PublishAndReceive(
        string topic,
        string payload,
        Func<MqttMessage, bool> isResponse,
        IScenarioContext context,
        TimeSpan? timeout = null,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce) =>
        PublishAndReceive(topic, Encoding.UTF8.GetBytes(payload), isResponse, context, timeout, qos);

    private Task<MqttClientPublishResult> Send(
        string topic, byte[] payload, MqttQualityOfServiceLevel qos, bool retain, CancellationToken cancellationToken)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(qos)
            .WithRetainFlag(retain)
            .Build();

        return _client.PublishAsync(message, cancellationToken);
    }

    /// <summary>The next message in the inbox, or null when the wait ran out.</summary>
    private async Task<MqttMessage?> Next(IScenarioContext context, TimeSpan? timeout)
    {
        using var timeoutCts = timeout is { } limit
            ? CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken)
            : null;

        timeoutCts?.CancelAfter(timeout!.Value);

        try
        {
            return await _inbox.Reader
                .ReadAsync(timeoutCts?.Token ?? context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true
                                                 && !context.CancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>What is left of a timeout that has already been partly spent.</summary>
    private static TimeSpan? Remaining(TimeSpan? timeout, long started)
    {
        if (timeout is not { } limit) return null;

        var left = limit - Stopwatch.GetElapsedTime(started);

        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private Task OnMessage(MqttApplicationMessageReceivedEventArgs args)
    {
        var payload = args.ApplicationMessage.Payload.ToArray();

        _inbox.Writer.TryWrite(new MqttMessage
        {
            Topic = args.ApplicationMessage.Topic,
            Payload = payload,
            SizeBytes = payload.Length,
            QualityOfService = args.ApplicationMessage.QualityOfServiceLevel,
            Retained = args.ApplicationMessage.Retain
        });

        return Task.CompletedTask;
    }

    private static Response<object> Failure(Exception ex, long started) =>
        Response.Fail() with
        {
            StatusCode = StatusCodeFor(ex),
            Message = ex.Message,
            LatencyMs = Elapsed(started)
        };

    private static Response<T> Timeout<T>(TimeSpan? timeout, long started) =>
        Response.FailOf<T>() with
        {
            StatusCode = MqttStatusCodes.ResponseTimeout,
            Message = $"nothing was delivered within {timeout}",
            LatencyMs = Elapsed(started)
        };

    private static string StatusCodeFor(Exception ex) =>
        ex is MQTTnet.Exceptions.MqttCommunicationException
            ? MqttStatusCodes.TransportError
            : MqttStatusCodes.BrokerError;

    private static double Elapsed(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.IsConnected)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _client.DisconnectAsync(cancellationToken: cts.Token).ConfigureAwait(false);
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
        _client.ApplicationMessageReceivedAsync -= OnMessage;
        _inbox.Writer.TryComplete();
        _client.Dispose();
    }
}

/// <summary>One message off a broker.</summary>
public sealed record MqttMessage
{
    public required string Topic { get; init; }
    public required byte[] Payload { get; init; }
    public required long SizeBytes { get; init; }
    public required MqttQualityOfServiceLevel QualityOfService { get; init; }
    public required bool Retained { get; init; }

    /// <summary>The payload as UTF-8 text.</summary>
    public string Text => Encoding.UTF8.GetString(Payload);
}

/// <summary>The status codes Autobahn reports for a broker that did not answer normally.</summary>
public static class MqttStatusCodes
{
    /// <summary>The connection itself failed: connect, reset, broker gone.</summary>
    public const string TransportError = "-220";

    /// <summary>The broker refused the message.</summary>
    public const string NotAccepted = "-221";

    /// <summary>Nothing was delivered within the timeout.</summary>
    public const string ResponseTimeout = "-222";

    /// <summary>The broker refused the subscription, so nothing was ever going to arrive.</summary>
    public const string SubscriptionRefused = "-223";

    /// <summary>A message arrived with no delivery stamp, so its delivery latency is unknown.</summary>
    public const string Unstamped = "-224";

    /// <summary>The broker answered, and what it said was an error.</summary>
    public const string BrokerError = "-225";
}
