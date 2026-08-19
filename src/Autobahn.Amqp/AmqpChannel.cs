using System.Diagnostics;
using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Autobahn.Amqp;

/// <summary>
/// One virtual user's AMQP channel, with the two shapes a load test needs from a broker.
/// </summary>
/// <remarks>
/// The same two shapes the MQTT and WebSocket helpers offer, because they are properties of
/// messaging rather than of a protocol:
///
/// - **Request/response**: publish, then wait for the message that answers it, correlated by
///   whatever the protocol above AMQP uses. AMQP has a convention for this - a reply queue and
///   a correlation id - and <see cref="PublishAndReceive"/> follows it.
/// - **Publish then consume**: one scenario publishes and another consumes, and what is being
///   measured is the delivery latency *between* them. <see cref="PublishStamped"/> and
///   <see cref="ReceiveStamped"/> are that pair.
///
/// **One of these per virtual user, and one channel each.** A connection is expensive and a
/// channel is not, so a pool shares one connection and gives every copy its own channel -
/// which is also what the protocol wants: a channel is not safe for concurrent use, and two
/// scenario copies publishing on one would serialise against each other and measure the lock.
///
/// Sharing the connection is the one place this differs from the MQTT helper, and it is not an
/// oversight: an MQTT connection *is* the session, so N users on one is a different test,
/// while an AMQP connection is a transport that multiplexes sessions by design.
/// </remarks>
public sealed class AmqpChannel : IAsyncDisposable
{
    private readonly IConnection? _ownedConnection;
    private readonly IChannel _channel;
    private readonly System.Threading.Channels.Channel<AmqpMessage> _inbox;

    private long _dropped;

    private AmqpChannel(IConnection? ownedConnection, IChannel channel, int inboxCapacity)
    {
        _ownedConnection = ownedConnection;
        _channel = channel;

        // Bounded, and dropping the oldest. An unbounded inbox in a load test is a memory leak
        // waiting for a slow consumer; dropping says the consumer could not keep up, which is a
        // finding rather than an error, and Dropped is where it is said.
        // Qualified: this class has a Channel property of its own, which is the name a user of
        // it wants and which shadows System.Threading.Channels.Channel in here.
        _inbox = System.Threading.Channels.Channel.CreateBounded<AmqpMessage>(
            new System.Threading.Channels.BoundedChannelOptions(inboxCapacity)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    /// <summary>The RabbitMQ channel underneath, for anything this class does not cover.</summary>
    public IChannel Channel => _channel;

    public bool IsOpen => _channel.IsOpen;

    /// <summary>
    /// How many delivered messages this channel had to throw away.
    /// </summary>
    /// <remarks>
    /// Not zero means the consumer scenario is slower than the broker is delivering, which
    /// makes every latency it reports optimistic. Worth putting on a gauge.
    /// </remarks>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>How many delivered messages are waiting to be read.</summary>
    public int Waiting => _inbox.Reader.Count;

    /// <summary>Opens a connection and one channel on it, both owned by the result.</summary>
    public static async Task<AmqpChannel> ConnectAsync(
        string uri = "amqp://guest:guest@localhost:5672/",
        Action<ConnectionFactory>? configure = null,
        int inboxCapacity = 1024,
        CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(uri, configure, cancellationToken).ConfigureAwait(false);

        try
        {
            var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new AmqpChannel(connection, channel, inboxCapacity);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// A pool of channels over one connection, one channel per virtual user.
    /// </summary>
    /// <remarks>
    /// The connection is disposed by the <see cref="AmqpPool"/> rather than by the
    /// channels, because they share it: a pool where the first copy to finish closes the
    /// transport out from under the rest is a pool that reports a cascade of connection errors
    /// as the test winds down.
    /// </remarks>
    public static async Task<AmqpPool> CreatePoolAsync(
        string uri,
        int count,
        Action<ConnectionFactory>? configure = null,
        int inboxCapacity = 1024,
        CancellationToken cancellationToken = default)
    {
        if (count < 1)
            throw new AutobahnException($"An AMQP pool of {count} channels is not something a scenario can use.");

        var connection = await OpenConnectionAsync(uri, configure, cancellationToken).ConfigureAwait(false);

        try
        {
            var pool = new ClientPool<AmqpChannel>();

            for (var i = 0; i < count; i++)
            {
                var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                pool.AddClient(new AmqpChannel(ownedConnection: null, channel, inboxCapacity));
            }

            return new AmqpPool(connection, pool);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static Task<IConnection> OpenConnectionAsync(
        string uri, Action<ConnectionFactory>? configure, CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory { Uri = new Uri(uri) };

        configure?.Invoke(factory);

        return factory.CreateConnectionAsync(cancellationToken);
    }

    /// <summary>
    /// Declares a queue and starts delivering it into this channel's inbox.
    /// </summary>
    /// <remarks>
    /// The setup form, for <c>WithInit</c>: there is no scenario context there, and a consumer
    /// that could not be registered is not a slow iteration to be recorded - it is a test that
    /// cannot run, so it throws.
    ///
    /// Auto-acknowledged by default. A load test measuring delivery wants the broker to stop
    /// tracking a message the moment it is handed over; a test that is specifically about
    /// acknowledgement turns it off and acknowledges through <see cref="Channel"/>.
    /// </remarks>
    public async Task ConsumeAsync(
        string queue,
        bool autoAcknowledge = true,
        ushort prefetch = 0,
        CancellationToken cancellationToken = default)
    {
        if (prefetch > 0)
        {
            await _channel.BasicQosAsync(0, prefetch, global: false, cancellationToken).ConfigureAwait(false);
        }

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnDelivery;

        await _channel.BasicConsumeAsync(queue, autoAcknowledge, consumer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Registers a consumer as a measured step inside an iteration.</summary>
    public async Task<Response<object>> Consume(
        string queue,
        IScenarioContext context,
        bool autoAcknowledge = true,
        ushort prefetch = 0)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            await ConsumeAsync(queue, autoAcknowledge, prefetch, context.CancellationToken).ConfigureAwait(false);

            return Response.Ok(latencyMs: Elapsed(started));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Response.Fail() with
            {
                StatusCode = StatusCodeFor(ex),
                Message = ex.Message,
                LatencyMs = Elapsed(started)
            };
        }
    }

    /// <summary>
    /// Declares a queue, creating it if the broker does not have it.
    /// </summary>
    /// <remarks>The setup form, for <c>WithInit</c>. See <see cref="ConsumeAsync"/>.</remarks>
    /// <returns>The queue's name, which the broker chooses when the requested one is empty.</returns>
    public async Task<string> DeclareQueueAsync(
        string queue,
        bool durable = false,
        bool exclusive = false,
        bool autoDelete = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _channel
            .QueueDeclareAsync(queue, durable, exclusive, autoDelete, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.QueueName;
    }

    /// <summary>Declares a queue as a measured step inside an iteration.</summary>
    public async Task<Response<string>> DeclareQueue(
        string queue,
        IScenarioContext context,
        bool durable = false,
        bool exclusive = false,
        bool autoDelete = true)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var name = await DeclareQueueAsync(queue, durable, exclusive, autoDelete, context.CancellationToken)
                .ConfigureAwait(false);

            return Response.Ok<string>(name, latencyMs: Elapsed(started));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Response.FailOf<string>() with
            {
                StatusCode = StatusCodeFor(ex),
                Message = ex.Message,
                LatencyMs = Elapsed(started)
            };
        }
    }

    /// <summary>Publishes a body and reports how long the broker took to take it.</summary>
    public async Task<Response<object>> Publish(
        string routingKey,
        byte[] body,
        IScenarioContext context,
        string exchange = "",
        BasicProperties? properties = null)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            await Send(exchange, routingKey, body, properties, context.CancellationToken).ConfigureAwait(false);

            return Response.Ok(sizeBytes: body.Length, latencyMs: Elapsed(started));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Response.Fail() with
            {
                StatusCode = StatusCodeFor(ex),
                Message = ex.Message,
                SizeBytes = body.Length,
                LatencyMs = Elapsed(started)
            };
        }
    }

    /// <summary>Publishes UTF-8 text.</summary>
    public Task<Response<object>> Publish(
        string routingKey,
        string body,
        IScenarioContext context,
        string exchange = "",
        BasicProperties? properties = null) =>
        Publish(routingKey, Encoding.UTF8.GetBytes(body), context, exchange, properties);

    /// <summary>
    /// Publishes a body with the time it was sent in a header.
    /// </summary>
    /// <remarks>
    /// The publisher half of the publish-then-consume shape. What it measures is still the
    /// broker taking the message - the delivery latency is measured by whoever receives it,
    /// which is the point of splitting the two.
    /// </remarks>
    public Task<Response<object>> PublishStamped(
        string routingKey,
        byte[] body,
        IScenarioContext context,
        string exchange = "",
        BasicProperties? properties = null) =>
        Publish(routingKey, body, context, exchange, AmqpDelivery.Stamp(properties ?? new BasicProperties()));

    /// <summary>Publishes UTF-8 text with the time it was sent in a header.</summary>
    public Task<Response<object>> PublishStamped(
        string routingKey,
        string body,
        IScenarioContext context,
        string exchange = "",
        BasicProperties? properties = null) =>
        PublishStamped(routingKey, Encoding.UTF8.GetBytes(body), context, exchange, properties);

    /// <summary>
    /// Waits for the next delivered message.
    /// </summary>
    /// <remarks>
    /// The latency reported is how long this iteration waited, which is a property of the test
    /// rather than of the broker: a consumer with nothing to consume waits for as long as the
    /// publisher takes. Use <see cref="ReceiveStamped"/> when the number that matters is how
    /// long the message took to arrive.
    /// </remarks>
    public async Task<Response<AmqpMessage>> Receive(IScenarioContext context, TimeSpan? timeout = null)
    {
        var started = Stopwatch.GetTimestamp();
        var message = await Next(context, timeout).ConfigureAwait(false);

        return message is null
            ? Timeout<AmqpMessage>(timeout, started)
            : Response.Ok(message, sizeBytes: message.SizeBytes, latencyMs: Elapsed(started));
    }

    /// <summary>
    /// Waits for the next delivered message and reports how long it took to get here.
    /// </summary>
    /// <remarks>
    /// The consumer half of the publish-then-consume shape, and the only one of these methods
    /// whose latency is about the broker rather than about the test.
    ///
    /// A message with no stamp is a failure rather than a zero. It means something other than
    /// this test is publishing to the queue, and silently reporting that as instant delivery
    /// would be the most flattering possible lie.
    /// </remarks>
    public async Task<Response<AmqpMessage>> ReceiveStamped(IScenarioContext context, TimeSpan? timeout = null)
    {
        var started = Stopwatch.GetTimestamp();
        var message = await Next(context, timeout).ConfigureAwait(false);

        if (message is null) return Timeout<AmqpMessage>(timeout, started);

        return message.DeliveryDelay is { } delay
            ? Response.Ok(message, sizeBytes: message.SizeBytes, latencyMs: delay.TotalMilliseconds)
            : Response.FailOf<AmqpMessage>() with
            {
                StatusCode = AmqpStatusCodes.Unstamped,
                Message = $"a message on '{message.RoutingKey}' carried no delivery stamp",
                SizeBytes = message.SizeBytes,
                LatencyMs = Elapsed(started)
            };
    }

    /// <summary>
    /// Publishes and waits for the message that answers it.
    /// </summary>
    /// <param name="isResponse">
    /// Which delivered message is the answer. AMQP's own convention is a correlation id on a
    /// reply queue, but only the protocol above knows which one is in use, so the caller says.
    /// </param>
    /// <remarks>
    /// Messages that arrive while waiting and are not the answer are dropped, not put back:
    /// this shape is for a channel whose conversation the test owns. A test where other traffic
    /// shares the queue wants a consumer scenario of its own instead.
    /// </remarks>
    public async Task<Response<AmqpMessage>> PublishAndReceive(
        string routingKey,
        byte[] body,
        Func<AmqpMessage, bool> isResponse,
        IScenarioContext context,
        TimeSpan? timeout = null,
        string exchange = "",
        BasicProperties? properties = null)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            await Send(exchange, routingKey, body, properties, context.CancellationToken).ConfigureAwait(false);

            while (true)
            {
                var message = await Next(context, Remaining(timeout, started)).ConfigureAwait(false);

                if (message is null) return Timeout<AmqpMessage>(timeout, started) with { SizeBytes = body.Length };
                if (!isResponse(message)) continue;

                return Response.Ok(
                    message,
                    sizeBytes: body.Length + message.SizeBytes,
                    latencyMs: Elapsed(started));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Response.FailOf<AmqpMessage>() with
            {
                StatusCode = StatusCodeFor(ex),
                Message = ex.Message,
                SizeBytes = body.Length,
                LatencyMs = Elapsed(started)
            };
        }
    }

    /// <summary>Publishes UTF-8 text and waits for the message that answers it.</summary>
    public Task<Response<AmqpMessage>> PublishAndReceive(
        string routingKey,
        string body,
        Func<AmqpMessage, bool> isResponse,
        IScenarioContext context,
        TimeSpan? timeout = null,
        string exchange = "",
        BasicProperties? properties = null) =>
        PublishAndReceive(routingKey, Encoding.UTF8.GetBytes(body), isResponse, context, timeout, exchange, properties);

    private ValueTask Send(
        string exchange,
        string routingKey,
        byte[] body,
        BasicProperties? properties,
        CancellationToken cancellationToken) =>
        properties is null
            ? _channel.BasicPublishAsync(exchange, routingKey, mandatory: false, body, cancellationToken)
            : _channel.BasicPublishAsync(exchange, routingKey, mandatory: false, properties, body, cancellationToken);

    /// <summary>The next message in the inbox, or null when the wait ran out.</summary>
    private async Task<AmqpMessage?> Next(IScenarioContext context, TimeSpan? timeout)
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

    /// <summary>
    /// Takes a delivery off the consumer and puts it in the inbox.
    /// </summary>
    /// <remarks>
    /// The delay is read here rather than when the scenario asks for the message: the point of
    /// the measurement is when the message *arrived*, and time spent queued behind a busy
    /// consumer would otherwise be counted as broker latency.
    /// </remarks>
    private Task OnDelivery(object sender, BasicDeliverEventArgs args)
    {
        var body = args.Body.ToArray();

        _inbox.Writer.TryWrite(new AmqpMessage
        {
            Exchange = args.Exchange,
            RoutingKey = args.RoutingKey,
            Body = body,
            SizeBytes = body.Length,
            DeliveryTag = args.DeliveryTag,
            Redelivered = args.Redelivered,
            CorrelationId = args.BasicProperties.CorrelationId,
            ReplyTo = args.BasicProperties.ReplyTo,
            DeliveryDelay = AmqpDelivery.TryRead(args.BasicProperties, out var delay) ? delay : null
        });

        return Task.CompletedTask;
    }

    private static Response<T> Timeout<T>(TimeSpan? timeout, long started) =>
        Response.FailOf<T>() with
        {
            StatusCode = AmqpStatusCodes.ResponseTimeout,
            Message = $"nothing was delivered within {timeout}",
            LatencyMs = Elapsed(started)
        };

    private static string StatusCodeFor(Exception ex) => ex switch
    {
        AlreadyClosedException => AmqpStatusCodes.Closed,
        BrokerUnreachableException => AmqpStatusCodes.TransportError,
        OperationInterruptedException => AmqpStatusCodes.BrokerError,
        RabbitMQClientException => AmqpStatusCodes.BrokerError,
        _ => AmqpStatusCodes.TransportError
    };

    private static double Elapsed(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _channel.CloseAsync().ConfigureAwait(false);
        }
        catch
        {
            // A channel that will not close politely is disposed anyway. A load test's teardown
            // is not the place to fail over a goodbye.
        }

        await _channel.DisposeAsync().ConfigureAwait(false);

        _inbox.Writer.TryComplete();

        if (_ownedConnection is not null) await _ownedConnection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// A pool of channels and the one connection they share.
/// </summary>
/// <remarks>
/// Its own type rather than a bare <c>ClientPool</c> because something has to own the
/// connection: the channels cannot, since the first copy to finish would close the transport
/// the others are still using.
/// </remarks>
public sealed class AmqpPool(IConnection connection, ClientPool<AmqpChannel> channels) : IAsyncDisposable
{
    /// <summary>The channels, handed out to scenario copies by index.</summary>
    public ClientPool<AmqpChannel> Channels => channels;

    /// <summary>The connection they all run on.</summary>
    public IConnection Connection => connection;

    public async ValueTask DisposeAsync()
    {
        foreach (var channel in channels.Clients) await channel.DisposeAsync().ConfigureAwait(false);

        await connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>One message off a broker.</summary>
public sealed record AmqpMessage
{
    public required string Exchange { get; init; }
    public required string RoutingKey { get; init; }
    public required byte[] Body { get; init; }
    public required long SizeBytes { get; init; }
    public required ulong DeliveryTag { get; init; }
    public required bool Redelivered { get; init; }

    public string? CorrelationId { get; init; }
    public string? ReplyTo { get; init; }

    /// <summary>How long this took to arrive, or null when it carried no stamp.</summary>
    public TimeSpan? DeliveryDelay { get; init; }

    /// <summary>The body as UTF-8 text.</summary>
    public string Text => Encoding.UTF8.GetString(Body);
}

/// <summary>The status codes Autobahn reports for a broker that did not answer normally.</summary>
public static class AmqpStatusCodes
{
    /// <summary>The connection itself failed: unreachable broker, reset, refused.</summary>
    public const string TransportError = "-230";

    /// <summary>The channel or connection was already closed when the iteration used it.</summary>
    public const string Closed = "-231";

    /// <summary>Nothing was delivered within the timeout.</summary>
    public const string ResponseTimeout = "-232";

    /// <summary>A message arrived with no delivery stamp, so its delivery latency is unknown.</summary>
    public const string Unstamped = "-233";

    /// <summary>The broker answered, and what it said was an error.</summary>
    public const string BrokerError = "-234";
}
