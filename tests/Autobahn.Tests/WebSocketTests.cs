using System.Net.WebSockets;
using Autobahn.Stats;
using Autobahn.WebSockets;

namespace Autobahn.Tests;

[NotInParallel]
public class WebSocketClientTests
{
    private static IScenarioContext Context(CancellationToken token = default) => new FakeContext(token);

    private static string WsAddress(TestServer server) => server.BaseAddress.Replace("http://", "ws://") + "ws";

    [Test]
    public async Task Sending_a_frame_reports_the_bytes_that_went_out()
    {
        await using var server = TestServer.Start();
        await using var client = await WebSocketClient.ConnectAsync(WsAddress(server));

        var response = await client.SendText("hello", Context());

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.SizeBytes).IsEqualTo(5);
    }

    [Test]
    public async Task Send_and_receive_skips_past_traffic_that_is_not_the_answer()
    {
        await using var server = TestServer.Start();
        await using var client = await WebSocketClient.ConnectAsync(WsAddress(server));

        // The test server pushes a heartbeat before every echo, so the predicate has something
        // to walk past - which is the whole reason correlation is the caller's job.
        var response = await client.SendAndReceive(
            "ping",
            message => message.Text.StartsWith("echo:", StringComparison.Ordinal),
            Context());

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Payload!.Text).IsEqualTo("echo:ping");
        await Assert.That(response.SizeBytes).IsEqualTo(4 + "echo:ping".Length);
    }

    [Test]
    public async Task Receive_hands_back_a_whole_message()
    {
        await using var server = TestServer.Start();
        await using var client = await WebSocketClient.ConnectAsync(WsAddress(server));

        await client.SendText("publish", Context());

        var first = await client.Receive(Context());
        var second = await client.Receive(Context());

        await Assert.That(first.Payload!.Text).IsEqualTo("push:heartbeat");
        await Assert.That(second.Payload!.Text).IsEqualTo("echo:publish");
    }

    [Test]
    public async Task An_answer_that_never_arrives_is_a_timeout_rather_than_a_hang()
    {
        await using var server = TestServer.Start();
        await using var client = await WebSocketClient.ConnectAsync(WsAddress(server));

        var response = await client.SendAndReceive(
            "ping",
            _ => false,   // nothing will ever satisfy this
            Context(),
            timeout: Time.Milliseconds(300));

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.StatusCode).IsEqualTo(WebSocketStatusCodes.ResponseTimeout);
    }

    [Test]
    public async Task A_connection_that_closes_mid_wait_says_so_rather_than_looking_like_a_timeout()
    {
        await using var server = TestServer.Start();
        await using var client = await WebSocketClient.ConnectAsync(WsAddress(server));

        var response = await client.SendAndReceive("close", _ => true, Context(), timeout: Time.Seconds(5));

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.StatusCode).IsEqualTo(WebSocketStatusCodes.Closed);
    }

    [Test]
    public async Task A_pool_gives_each_copy_its_own_connection()
    {
        await using var server = TestServer.Start();

        var pool = await WebSocketClient.CreatePoolAsync(WsAddress(server), count: 3);

        try
        {
            await Assert.That(pool.Clients.Count).IsEqualTo(3);
            await Assert.That(pool.Clients.All(x => x.State == WebSocketState.Open)).IsTrue();

            var first = pool.GetClient(Info(0));

            // A WebSocket is a session, so the same copy must keep the same one.
            await Assert.That(pool.GetClient(Info(0))).IsSameReferenceAs(first);
            await Assert.That(pool.GetClient(Info(1))).IsNotSameReferenceAs(first);
        }
        finally
        {
            foreach (var client in pool.Clients) await client.DisposeAsync();
        }
    }

    [Test]
    public async Task A_pool_of_no_connections_is_refused()
    {
        await using var server = TestServer.Start();

        await Assert.That(await Assert.ThrowsAsync<AutobahnException>(
            async () => await WebSocketClient.CreatePoolAsync(WsAddress(server), 0))).IsNotNull();
    }

    private static ScenarioInfo Info(int threadNumber) => new()
    {
        ThreadId = $"copy_{threadNumber}",
        ThreadNumber = threadNumber,
        ScenarioName = "scn",
        ScenarioDuration = TimeSpan.FromSeconds(1),
        CopyCount = 3,
        ScenarioOperation = ScenarioOperation.Bombing
    };

    private sealed class FakeContext(CancellationToken token) : IScenarioContext
    {
        public TestInfo TestInfo => TestInfo.Empty;
        public ScenarioInfo ScenarioInfo => Info(0);
        public HostInfo HostInfo => HostInfo.Empty;

        public Microsoft.Extensions.Logging.ILogger Logger { get; } =
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public Autobahn.Metrics.IMetricRegistry Metrics { get; } =
            new Autobahn.Internal.Domain.Metrics.MetricRegistry();

        public int InvocationNumber => 1;
        public Dictionary<string, object> Data { get; } = [];
        public CancellationToken CancellationToken => token;

        public void StopScenario(string scenarioName, string reason) { }
        public void StopCurrentTest(string reason) { }
    }
}
