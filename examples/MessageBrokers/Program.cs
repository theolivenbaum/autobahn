using Autobahn;
using Autobahn.Amqp;
using Autobahn.Mqtt;
using Autobahn.Thresholds;
using static Autobahn.Thresholds.ThresholdSubject;

// Load-testing a message broker, in the two shapes a broker test actually comes in.
//
//   1. Request/response, where one virtual user publishes and waits for the answer. What it
//      measures is a round trip, and it looks like any other client/server test.
//
//   2. Publish-then-consume, where a publisher scenario and a consumer scenario are separate
//      and neither knows about the other. What that measures is end-to-end delivery latency -
//      how long the broker took to get a message from one side to the other - which is the
//      number a broker test usually exists for.
//
// The second shape needs the send time to travel with the message, because the consumer has
// no other way to know it. `PublishStamped`/`ReceiveStamped` are that: a stamp the publisher
// writes and the consumer reads back, in the payload for MQTT (which has no user properties
// before v5) and in a header for AMQP (which does). A message that arrives without one is a
// failure rather than a zero - a broker that dropped the stamp is not a fast broker.
//
// Run it against a broker of your own:
//
//   dotnet run --project examples/MessageBrokers -- mqtt localhost
//   dotnet run --project examples/MessageBrokers -- amqp amqp://guest:guest@localhost:5672/

var which = args.FirstOrDefault(x => !x.StartsWith('-'))?.ToLowerInvariant() ?? "mqtt";
var target = args.Where(x => !x.StartsWith('-')).Skip(1).FirstOrDefault();

if (which == "amqp") await RunAmqp(target ?? "amqp://guest:guest@localhost:5672/", args);
else await RunMqtt(target ?? "localhost", args);

static async Task RunMqtt(string host, string[] args)
{
    const string requestTopic = "autobahn/example/request";
    const string replyTopic = "autobahn/example/reply";
    const string streamTopic = "autobahn/example/stream";

    // Shape 1. One connection per virtual user: a pool sized to the load, handed out by copy
    // index, because two copies sharing a connection are one client with twice the traffic.
    using var clients = await MqttConnection.CreatePoolAsync(host, count: 10);

    foreach (var client in clients.Clients)
        await client.SubscribeAsync(replyTopic);

    var roundTrip = Scenario.Create("mqtt_round_trip", async context =>
        {
            var client = clients.GetClient(context.ScenarioInfo);

            return await client.PublishAndReceive(
                requestTopic,
                $"ping {context.InvocationNumber}",
                // Which delivered message answers this request. A correlation id in the
                // payload would be the honest version; the example keeps it simple.
                isResponse: message => message.Topic == replyTopic,
                context,
                timeout: TimeSpan.FromSeconds(2));
        })
        .WithoutWarmUp()
        .WithLoadSimulations(Simulation.KeepConstant(copies: 10, during: Time.Seconds(30)));

    // Shape 2. The publisher and the consumer each own one connection and never refer to each
    // other. The consumer's latency *is* the delivery latency.
    await using var producer = await MqttConnection.ConnectAsync(host);
    await using var consumer = await MqttConnection.ConnectAsync(host);

    await consumer.SubscribeAsync(streamTopic);

    var publish = Scenario.Create("mqtt_publish", async context =>
            await producer.PublishStamped(streamTopic, $"event {context.InvocationNumber}", context))
        .WithoutWarmUp()
        .WithLoadSimulations(Simulation.Inject(rate: 200, interval: Time.Seconds(1), during: Time.Seconds(30)));

    var consume = Scenario.Create("mqtt_consume", async context =>
            await consumer.ReceiveStamped(context, timeout: TimeSpan.FromSeconds(2)))
        .WithoutWarmUp()
        // One long-lived consumer, looping. The open model would inject a fresh consumer per
        // interval, which measures how fast copies can be started rather than how fast
        // messages arrive.
        .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(30)));

    AutobahnRunner
        .RegisterScenarios(roundTrip, publish, consume)
        .WithTestSuite("brokers")
        .WithTestName("mqtt")
        .WithThresholds(
            Threshold.ErrorRateBelow(0.01),
            // Delivery, not round trip: this one is about the consumer only.
            Threshold.LatencyBelow(Percent99, 250).ForScenario("mqtt_consume"))
        .Run(args);
}

static async Task RunAmqp(string uri, string[] args)
{
    const string requestQueue = "autobahn.example.request";
    const string streamQueue = "autobahn.example.stream";

    // Shape 1. Channels rather than connections: AMQP multiplexes, so a pool of channels over
    // one connection is what a client library is meant to do. The pool owns the connection,
    // so the first copy to finish does not close the transport out from under the rest.
    await using var pool = await AmqpChannel.CreatePoolAsync(uri, count: 10);

    foreach (var channel in pool.Channels.Clients)
    {
        await channel.DeclareQueueAsync(requestQueue);
        await channel.ConsumeAsync(await channel.DeclareQueueAsync(queue: "", exclusive: true));
    }

    var roundTrip = Scenario.Create("amqp_round_trip", async context =>
        {
            var channel = pool.Channels.GetClient(context.ScenarioInfo);

            return await channel.PublishAndReceive(
                requestQueue,
                $"ping {context.InvocationNumber}",
                isResponse: _ => true,
                context,
                timeout: TimeSpan.FromSeconds(2));
        })
        .WithoutWarmUp()
        .WithLoadSimulations(Simulation.KeepConstant(copies: 10, during: Time.Seconds(30)));

    // Shape 2, as above: two channels that know nothing about each other.
    await using var producer = await AmqpChannel.ConnectAsync(uri);
    await using var consumer = await AmqpChannel.ConnectAsync(uri);

    await producer.DeclareQueueAsync(streamQueue);
    await consumer.ConsumeAsync(streamQueue);

    var publish = Scenario.Create("amqp_publish", async context =>
            await producer.PublishStamped(streamQueue, $"event {context.InvocationNumber}", context))
        .WithoutWarmUp()
        .WithLoadSimulations(Simulation.Inject(rate: 200, interval: Time.Seconds(1), during: Time.Seconds(30)));

    var consume = Scenario.Create("amqp_consume", async context =>
            await consumer.ReceiveStamped(context, timeout: TimeSpan.FromSeconds(2)))
        .WithoutWarmUp()
        .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(30)));

    AutobahnRunner
        .RegisterScenarios(roundTrip, publish, consume)
        .WithTestSuite("brokers")
        .WithTestName("amqp")
        .WithThresholds(
            Threshold.ErrorRateBelow(0.01),
            Threshold.LatencyBelow(Percent99, 250).ForScenario("amqp_consume"))
        .Run(args);
}
