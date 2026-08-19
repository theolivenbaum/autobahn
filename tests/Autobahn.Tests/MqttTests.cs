using System.Net;
using System.Net.Sockets;
using Autobahn.Mqtt;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace Autobahn.Tests;

/// <summary>
/// A broker in this process, so the MQTT helper is tested against a real one.
/// </summary>
/// <remarks>
/// MQTTnet ships a server, which is the whole reason these tests exist rather than being
/// gated behind "start a broker first": a helper nothing has ever run is worse than none, and
/// a test that only runs on a machine somebody configured is a test that does not run.
/// </remarks>
internal sealed class MqttBroker : IAsyncDisposable
{
    private readonly MqttServer _server;

    private MqttBroker(MqttServer server, int port)
    {
        _server = server;
        Port = port;
    }

    public int Port { get; }

    public static async Task<MqttBroker> StartAsync()
    {
        var port = FreePort();

        var options = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(port)
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            // IPAddress.None turns the v6 listener off. Containers routinely have no IPv6 at
            // all, and MQTTnet's default is to bind both - which fails the whole server start
            // with "address family not supported" rather than skipping the half that cannot work.
            .WithDefaultEndpointBoundIPV6Address(IPAddress.None)
            .Build();

        var server = new MqttServerFactory().CreateMqttServer(options);
        await server.StartAsync();

        return new MqttBroker(server, port);
    }

    /// <summary>
    /// A port nothing is listening on, by listening on one and letting go.
    /// </summary>
    /// <remarks>
    /// Racy in principle and fine in practice, and the alternative - a fixed port - is racy
    /// against every other test run on the machine rather than against a two-microsecond window.
    /// </remarks>
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);

        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        _server.Dispose();
    }
}

[NotInParallel]
internal class MqttConnectionTests
{
    private static IScenarioContext Context(CancellationToken token = default) => new FakeScenarioContext(token);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Publishing_reports_the_bytes_that_went_out()
    {
        await using var broker = await MqttBroker.StartAsync();
        await using var connection = await MqttConnection.ConnectAsync("127.0.0.1", broker.Port);

        var response = await connection.Publish("sensors/one", "24.5", Context());

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.SizeBytes).IsEqualTo(4);
    }

    [Test]
    public async Task A_subscriber_receives_what_a_publisher_sent()
    {
        await using var broker = await MqttBroker.StartAsync();
        await using var consumer = await MqttConnection.ConnectAsync("127.0.0.1", broker.Port);
        await using var publisher = await MqttConnection.ConnectAsync("127.0.0.1", broker.Port);

        await consumer.Subscribe("sensors/#", Context(), MqttQualityOfServiceLevel.AtLeastOnce);
        await publisher.Publish("sensors/two", "24.5", Context(), MqttQualityOfServiceLevel.AtLeastOnce);

        var received = await consumer.Receive(Context(), Patience);

        await Assert.That(received.IsError).IsFalse();
        await Assert.That(received.Payload!.Topic).IsEqualTo("sensors/two");
        await Assert.That(received.Payload.Text).IsEqualTo("24.5");
    }

    /// <summary>
    /// The publish-then-consume shape: two independent scenarios, and the number that matters
    /// is the time between them rather than either side's own speed.
    /// </summary>
    [Test]
    public async Task A_stamped_message_reports_how_long_delivery_took()
    {
        await using var broker = await MqttBroker.StartAsync();
        await using var consumer = await MqttConnection.ConnectAsync("127.0.0.1", broker.Port);
        await using var publisher = await MqttConnection.ConnectAsync("127.0.0.1", broker.Port);

        await consumer.Subscribe("events/#", Context(), MqttQualityOfServiceLevel.AtLeastOnce);

        await publisher.PublishStamped("events/order", "placed", Context(), MqttQualityOfServiceLevel.AtLeastOnce);

        var received = await consumer.ReceiveStamped(Context(), Patience);

        await Assert.That(received.IsError).IsFalse();

        // The stamp is stripped: what the scenario reads is what was published.
        await Assert.That(received.Payload!.Text).IsEqualTo("placed");
        await Assert.That(received.SizeBytes).IsEqualTo("placed".Length);

        // Delivery over loopback is fast but not instant, and it is certainly not the time the
        // consumer spent waiting - which is what an unstamped receive would have reported.
        await Assert.That(received.LatencyMs).IsGreaterThan(0);
        await Assert.That(received.LatencyMs).IsLessThan(Patience.TotalMilliseconds);
    }

    /// <summary>
    /// A message from somewhere else is unstamped, and reporting it as instant delivery would
    /// be the most flattering possible lie.
    /// </summary>
    [Test]
    public async Task An_unstamped_message_is_a_failure_rather_than_a_zero()
    {
        await using var broker = await MqttBroker.StartAsync();
        await using var consumer = await MqttConnection.ConnectAsync("127.0.0.1", broker.Port);
        await using var publisher = await MqttConnection.ConnectAsync("127.0.0.1", broker.Port);

        await consumer.Subscribe("events/#", Context(), MqttQualityOfServiceLevel.AtLeastOnce);
        await publisher.Publish("events/foreign", "not ours", Context(), MqttQualityOfServiceLevel.AtLeastOnce);

        var received = await consumer.ReceiveStamped(Context(), Patience);

        await Assert.That(received.IsError).IsTrue();
        await Assert.That(received.StatusCode).IsEqualTo(MqttStatusCodes.Unstamped);
    }

    [Test]
    public async Task Publish_and_receive_walks_past_traffic_that_is_not_the_answer()
    {
        await using var broker = await MqttBroker.StartAsync();
        await using var client = await MqttConnection.ConnectAsync("127.0.0.1", broker.Port);

        await client.Subscribe("rpc/#", Context(), MqttQualityOfServiceLevel.AtLeastOnce);

        // Noise on the same subscription, published before the request, so the predicate has
        // something to walk past - which is the whole reason correlation is the caller's job.
        await client.Publish("rpc/notice", "unrelated", Context(), MqttQualityOfServiceLevel.AtLeastOnce);

        var response = await client.PublishAndReceive(
            "rpc/request",
            "id-7",
            message => message.Topic == "rpc/request",
            Context(),
            Patience,
            MqttQualityOfServiceLevel.AtLeastOnce);

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Payload!.Text).IsEqualTo("id-7");
    }

    [Test]
    public async Task An_answer_that_never_arrives_is_a_timeout_rather_than_a_hang()
    {
        await using var broker = await MqttBroker.StartAsync();
        await using var client = await MqttConnection.ConnectAsync("127.0.0.1", broker.Port);

        var response = await client.Receive(Context(), TimeSpan.FromMilliseconds(200));

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.StatusCode).IsEqualTo(MqttStatusCodes.ResponseTimeout);
    }

    /// <summary>
    /// One connection per virtual user, because N users on one connection are one user
    /// publishing N times as much and the broker sees one session rather than N.
    /// </summary>
    [Test]
    public async Task A_pool_opens_one_connection_per_copy()
    {
        await using var broker = await MqttBroker.StartAsync();

        var pool = await MqttConnection.CreatePoolAsync("127.0.0.1", count: 3, port: broker.Port);

        try
        {
            await Assert.That(pool.Clients.Count).IsEqualTo(3);
            await Assert.That(pool.Clients.All(x => x.IsConnected)).IsTrue();

            // Distinct sessions, not the same one three times.
            await Assert.That(pool.Clients.Select(x => x.Client.Options.ClientId).Distinct().Count()).IsEqualTo(3);
        }
        finally
        {
            foreach (var client in pool.Clients) await client.DisposeAsync();
        }
    }

    [Test]
    public async Task A_pool_of_no_connections_says_so()
    {
        await using var broker = await MqttBroker.StartAsync();

        await Assert.That(async () => await MqttConnection.CreatePoolAsync("127.0.0.1", count: 0, port: broker.Port))
            .Throws<AutobahnException>();
    }

    /// <summary>
    /// A consumer slower than the broker is delivering loses messages, and the count is how a
    /// scenario finds out - every latency it reports afterwards is optimistic.
    /// </summary>
    [Test]
    public async Task A_consumer_that_cannot_keep_up_says_how_much_it_lost()
    {
        await using var broker = await MqttBroker.StartAsync();
        await using var consumer = await MqttConnection.ConnectAsync("127.0.0.1", broker.Port, inboxCapacity: 4);
        await using var publisher = await MqttConnection.ConnectAsync("127.0.0.1", broker.Port);

        await consumer.Subscribe("flood/#", Context(), MqttQualityOfServiceLevel.AtLeastOnce);

        for (var i = 0; i < 40; i++)
        {
            await publisher.Publish("flood/x", i.ToString(), Context(), MqttQualityOfServiceLevel.AtLeastOnce);
        }

        // Nothing has read the inbox, so everything past its capacity had to go.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (consumer.Dropped == 0 && DateTime.UtcNow < deadline) await Task.Delay(25);

        await Assert.That(consumer.Dropped).IsGreaterThan(0);
        await Assert.That(consumer.Waiting).IsLessThanOrEqualTo(4);
    }

    [Test]
    public async Task A_broker_that_is_not_there_fails_rather_than_hangs()
    {
        // A port nothing is listening on: connecting has to end, and end as an error.
        await Assert.That(async () => await MqttConnection.ConnectAsync(
                "127.0.0.1",
                port: 1,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token))
            .ThrowsException();
    }
}

internal class MqttDeliveryTests
{
    [Test]
    public async Task A_stamp_round_trips_the_body_and_a_delay()
    {
        var stamped = MqttDelivery.Stamp("hello"u8.ToArray());

        await Assert.That(stamped.Length).IsEqualTo(MqttDelivery.Length + 5);

        await Task.Delay(20);

        await Assert.That(MqttDelivery.TryRead(stamped, out var delay, out var body)).IsTrue();
        await Assert.That(System.Text.Encoding.UTF8.GetString(body)).IsEqualTo("hello");
        await Assert.That(delay.TotalMilliseconds).IsGreaterThanOrEqualTo(15);
    }

    [Test]
    public async Task An_empty_body_still_carries_a_stamp()
    {
        var stamped = MqttDelivery.Stamp([]);

        await Assert.That(MqttDelivery.TryRead(stamped, out _, out var body)).IsTrue();
        await Assert.That(body.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments(new byte[0])]
    [Arguments(new byte[] { 1, 2, 3 })]
    public async Task Anything_this_test_did_not_stamp_is_refused(byte[] payload)
    {
        await Assert.That(MqttDelivery.TryRead(payload, out var delay, out var body)).IsFalse();
        await Assert.That(delay).IsEqualTo(TimeSpan.Zero);
        await Assert.That(body).IsEqualTo(payload);
    }

    /// <summary>
    /// A payload that happens to start with the magic bytes but carries a timestamp from
    /// somewhere else. Reporting that as a negative latency would be worse than refusing it.
    /// </summary>
    [Test]
    public async Task A_stamp_from_the_future_is_refused()
    {
        var forged = new byte[MqttDelivery.Length];

        "AbTs"u8.CopyTo(forged);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
            forged.AsSpan(4), System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 60);

        await Assert.That(MqttDelivery.TryRead(forged, out _, out _)).IsFalse();
    }
}
