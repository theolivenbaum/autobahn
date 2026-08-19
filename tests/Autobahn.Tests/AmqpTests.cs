using System.Net.Sockets;
using Autobahn.Amqp;
using RabbitMQ.Client;
using TUnit.Core.Exceptions;

namespace Autobahn.Tests;

/// <summary>
/// Whether there is a broker to talk to, and where.
/// </summary>
/// <remarks>
/// Unlike MQTT, AMQP has no in-process broker to test against: RabbitMQ is a server, and
/// nothing on NuGet implements the protocol well enough to stand in for one. So the
/// integration tests below run wherever a broker is and skip where one is not, rather than
/// failing on every machine that has not been set up.
///
/// That is a real gap and worth naming: the parts of this helper that only a broker can
/// exercise are covered on a machine with one and not on a machine without. Everything that
/// can be tested without a broker - the delivery stamp, the guards, the failure path when
/// nothing is listening - is tested everywhere.
///
/// Point it at a broker with <c>AUTOBAHN_AMQP_URI</c>, or run one locally:
/// <c>docker run --rm -p 5672:5672 rabbitmq:4-alpine</c>.
/// </remarks>
internal static class AmqpBroker
{
    public static string Uri =>
        Environment.GetEnvironmentVariable("AUTOBAHN_AMQP_URI") ?? "amqp://guest:guest@localhost:5672/";

    /// <summary>Skips the calling test when nothing is listening where the broker should be.</summary>
    public static void RequireBroker()
    {
        if (IsListening()) return;

        throw new SkipTestException(
            $"No AMQP broker at {Uri}. Start one, or set AUTOBAHN_AMQP_URI, to run this test.");
    }

    private static bool IsListening()
    {
        try
        {
            var uri = new Uri(Uri);
            var port = uri.Port > 0 ? uri.Port : 5672;

            using var probe = new TcpClient();

            // Short: this runs before every broker test, and a machine with no broker should
            // find that out in milliseconds rather than at the end of a connect timeout.
            return probe.ConnectAsync(uri.Host, port).Wait(TimeSpan.FromMilliseconds(500))
                   && probe.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string Queue(string prefix) => $"autobahn-{prefix}-{Guid.NewGuid():N}";
}

[NotInParallel]
internal class AmqpChannelTests
{
    private static IScenarioContext Context(CancellationToken token = default) => new FakeScenarioContext(token);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Publishing_reports_the_bytes_that_went_out()
    {
        AmqpBroker.RequireBroker();

        await using var channel = await AmqpChannel.ConnectAsync(AmqpBroker.Uri);

        var queue = await channel.DeclareQueue(AmqpBroker.Queue("publish"), Context());
        var response = await channel.Publish(queue.Payload!, "24.5", Context());

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.SizeBytes).IsEqualTo(4);
    }

    [Test]
    public async Task A_consumer_receives_what_a_publisher_sent()
    {
        AmqpBroker.RequireBroker();

        await using var consumer = await AmqpChannel.ConnectAsync(AmqpBroker.Uri);
        await using var publisher = await AmqpChannel.ConnectAsync(AmqpBroker.Uri);

        var name = AmqpBroker.Queue("consume");

        await consumer.DeclareQueue(name, Context());
        await consumer.Consume(name, Context());

        await publisher.Publish(name, "24.5", Context());

        var received = await consumer.Receive(Context(), Patience);

        await Assert.That(received.IsError).IsFalse();
        await Assert.That(received.Payload!.RoutingKey).IsEqualTo(name);
        await Assert.That(received.Payload.Text).IsEqualTo("24.5");
    }

    /// <summary>
    /// The publish-then-consume shape: two independent scenarios, and the number that matters
    /// is the time between them rather than either side's own speed.
    /// </summary>
    [Test]
    public async Task A_stamped_message_reports_how_long_delivery_took()
    {
        AmqpBroker.RequireBroker();

        await using var consumer = await AmqpChannel.ConnectAsync(AmqpBroker.Uri);
        await using var publisher = await AmqpChannel.ConnectAsync(AmqpBroker.Uri);

        var name = AmqpBroker.Queue("stamped");

        await consumer.DeclareQueue(name, Context());
        await consumer.Consume(name, Context());

        await publisher.PublishStamped(name, "placed", Context());

        var received = await consumer.ReceiveStamped(Context(), Patience);

        await Assert.That(received.IsError).IsFalse();

        // The stamp is a header, so the body is exactly what was published - which is the
        // reason it is a header rather than a prefix on the body.
        await Assert.That(received.Payload!.Text).IsEqualTo("placed");

        await Assert.That(received.LatencyMs).IsGreaterThan(0);
        await Assert.That(received.LatencyMs).IsLessThan(Patience.TotalMilliseconds);
    }

    [Test]
    public async Task An_unstamped_message_is_a_failure_rather_than_a_zero()
    {
        AmqpBroker.RequireBroker();

        await using var consumer = await AmqpChannel.ConnectAsync(AmqpBroker.Uri);
        await using var publisher = await AmqpChannel.ConnectAsync(AmqpBroker.Uri);

        var name = AmqpBroker.Queue("foreign");

        await consumer.DeclareQueue(name, Context());
        await consumer.Consume(name, Context());

        await publisher.Publish(name, "not ours", Context());

        var received = await consumer.ReceiveStamped(Context(), Patience);

        await Assert.That(received.IsError).IsTrue();
        await Assert.That(received.StatusCode).IsEqualTo(AmqpStatusCodes.Unstamped);
    }

    [Test]
    public async Task Publish_and_receive_walks_past_traffic_that_is_not_the_answer()
    {
        AmqpBroker.RequireBroker();

        await using var channel = await AmqpChannel.ConnectAsync(AmqpBroker.Uri);

        var name = AmqpBroker.Queue("rpc");

        await channel.DeclareQueue(name, Context());
        await channel.Consume(name, Context());

        // Noise on the same queue, published first, so the predicate has something to walk
        // past - which is the whole reason correlation is the caller's job.
        await channel.Publish(name, "unrelated", Context(), properties: new BasicProperties { CorrelationId = "other" });

        var response = await channel.PublishAndReceive(
            name,
            "the-request",
            message => message.CorrelationId == "mine",
            Context(),
            Patience,
            properties: new BasicProperties { CorrelationId = "mine" });

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Payload!.Text).IsEqualTo("the-request");
    }

    [Test]
    public async Task An_answer_that_never_arrives_is_a_timeout_rather_than_a_hang()
    {
        AmqpBroker.RequireBroker();

        await using var channel = await AmqpChannel.ConnectAsync(AmqpBroker.Uri);

        var response = await channel.Receive(Context(), TimeSpan.FromMilliseconds(200));

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.StatusCode).IsEqualTo(AmqpStatusCodes.ResponseTimeout);
    }

    /// <summary>
    /// One channel per copy over one connection: a channel is not safe for concurrent use, so
    /// two copies sharing one would serialise against each other and measure the lock.
    /// </summary>
    [Test]
    public async Task A_pool_opens_one_channel_per_copy_over_one_connection()
    {
        AmqpBroker.RequireBroker();

        await using var pool = await AmqpChannel.CreatePoolAsync(AmqpBroker.Uri, count: 3);

        await Assert.That(pool.Channels.Clients.Count).IsEqualTo(3);
        await Assert.That(pool.Channels.Clients.All(x => x.IsOpen)).IsTrue();
        await Assert.That(pool.Connection.IsOpen).IsTrue();

        // Distinct channels, not the same one three times.
        await Assert.That(pool.Channels.Clients.Select(x => x.Channel.ChannelNumber).Distinct().Count()).IsEqualTo(3);
    }

    /// <summary>
    /// A consumer slower than the broker is delivering loses messages, and the count is how a
    /// scenario finds out - every latency it reports afterwards is optimistic.
    /// </summary>
    [Test]
    public async Task A_consumer_that_cannot_keep_up_says_how_much_it_lost()
    {
        AmqpBroker.RequireBroker();

        await using var consumer = await AmqpChannel.ConnectAsync(AmqpBroker.Uri, inboxCapacity: 4);
        await using var publisher = await AmqpChannel.ConnectAsync(AmqpBroker.Uri);

        var name = AmqpBroker.Queue("flood");

        await consumer.DeclareQueue(name, Context());
        await consumer.Consume(name, Context());

        for (var i = 0; i < 60; i++) await publisher.Publish(name, i.ToString(), Context());

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (consumer.Dropped == 0 && DateTime.UtcNow < deadline) await Task.Delay(25);

        await Assert.That(consumer.Dropped).IsGreaterThan(0);
        await Assert.That(consumer.Waiting).IsLessThanOrEqualTo(4);
    }
}

/// <summary>What can be tested without a broker, which is more than nothing.</summary>
internal class AmqpWithoutABrokerTests
{
    [Test]
    public async Task A_pool_of_no_channels_says_so()
    {
        await Assert.That(async () => await AmqpChannel.CreatePoolAsync(AmqpBroker.Uri, count: 0))
            .Throws<AutobahnException>();
    }

    /// <summary>
    /// A broker that is not there has to fail, and fail promptly. This is the one integration
    /// path that needs no broker, which is why it runs everywhere.
    /// </summary>
    [Test]
    public async Task A_broker_that_is_not_there_fails_rather_than_hangs()
    {
        // Port 1: nothing listens there, on any machine.
        await Assert.That(async () => await AmqpChannel.ConnectAsync(
                "amqp://guest:guest@127.0.0.1:1/",
                configure: factory => factory.RequestedConnectionTimeout = TimeSpan.FromSeconds(2),
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token))
            .ThrowsException();
    }
}

internal class AmqpDeliveryTests
{
    [Test]
    public async Task A_stamp_round_trips_as_a_delay()
    {
        var properties = AmqpDelivery.Stamped();

        await Task.Delay(20);

        await Assert.That(AmqpDelivery.TryRead(Readable(properties), out var delay)).IsTrue();
        await Assert.That(delay.TotalMilliseconds).IsGreaterThanOrEqualTo(15);
    }

    /// <summary>The stamp is added to whatever headers the caller already set, not instead of them.</summary>
    [Test]
    public async Task Stamping_keeps_the_headers_that_were_already_there()
    {
        var properties = new BasicProperties
        {
            Headers = new Dictionary<string, object?> { ["x-tenant"] = "acme" }
        };

        AmqpDelivery.Stamp(properties);

        await Assert.That(properties.Headers!.ContainsKey("x-tenant")).IsTrue();
        await Assert.That(properties.Headers.ContainsKey(AmqpDelivery.Header)).IsTrue();
    }

    [Test]
    public async Task Properties_with_no_headers_at_all_are_refused()
    {
        await Assert.That(AmqpDelivery.TryRead(Readable(new BasicProperties()), out var delay)).IsFalse();
        await Assert.That(delay).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task A_header_that_is_not_a_stamp_is_refused()
    {
        var properties = new BasicProperties
        {
            Headers = new Dictionary<string, object?> { [AmqpDelivery.Header] = "not a timestamp" }
        };

        await Assert.That(AmqpDelivery.TryRead(Readable(properties), out _)).IsFalse();
    }

    /// <summary>
    /// A stamp written by another process, whose ticks mean something else. Reporting that as a
    /// negative latency would be worse than refusing it.
    /// </summary>
    [Test]
    public async Task A_stamp_from_the_future_is_refused()
    {
        var forged = new byte[sizeof(long)];

        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
            forged,
            System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 60);

        var properties = new BasicProperties
        {
            Headers = new Dictionary<string, object?> { [AmqpDelivery.Header] = forged }
        };

        await Assert.That(AmqpDelivery.TryRead(Readable(properties), out _)).IsFalse();
    }

    /// <summary>What arrives on a delivery is the read-only face of what was published.</summary>
    private static IReadOnlyBasicProperties Readable(BasicProperties properties) => properties;
}
