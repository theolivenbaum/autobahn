using Autobahn.Http;
using Autobahn.Stats;

namespace Autobahn.Tests;

/// <summary>
/// The HTTP helper against a real server on a real socket. A fake handler would test only
/// the code that calls one.
/// </summary>
[NotInParallel]
public class HttpRequestTests
{
    private static IScenarioContext Context() => new FakeContext();

    [Test]
    public async Task A_successful_request_reports_its_status_code_and_size()
    {
        await using var server = TestServer.Start();
        using var client = HttpClientPool.CreateClient();

        var response = await client.Send(HttpRequest.Get($"{server.BaseAddress}ok"), Context());

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.StatusCode).IsEqualTo("200");

        // The body is 15 bytes; everything above that is the request line, the status line and
        // the headers on both sides - which is the point of counting them.
        await Assert.That(response.SizeBytes).IsGreaterThan(100);
    }

    [Test]
    public async Task A_5xx_is_a_failure_without_anyone_having_to_say_so()
    {
        await using var server = TestServer.Start();
        using var client = HttpClientPool.CreateClient();

        var response = await client.Send(HttpRequest.Get($"{server.BaseAddress}error"), Context());

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.StatusCode).IsEqualTo("500");
    }

    [Test]
    public async Task A_status_check_makes_the_expected_code_the_only_success()
    {
        await using var server = TestServer.Start();
        using var client = HttpClientPool.CreateClient();

        var wanted201 = await client.Send(
            HttpRequest.Get($"{server.BaseAddress}created").WithStatusCheck(201), Context());

        var wanted200 = await client.Send(
            HttpRequest.Get($"{server.BaseAddress}created").WithStatusCheck(200), Context());

        await Assert.That(wanted201.IsError).IsFalse();
        await Assert.That(wanted200.IsError).IsTrue();
        await Assert.That(wanted200.StatusCode).IsEqualTo("201");
        await Assert.That(wanted200.Message).Contains("status is 200");
    }

    [Test]
    public async Task A_body_check_catches_a_200_that_is_not_actually_ok()
    {
        await using var server = TestServer.Start();
        using var client = HttpClientPool.CreateClient();

        var url = $"{server.BaseAddress}lying";

        var unchecked_ = await client.Send(HttpRequest.Get(url), Context());
        var checked_ = await client.Send(
            HttpRequest.Get(url).WithCheck(HttpCheck.Create("no error in body", (_, body) => !body.Contains("error"))),
            Context());

        // An API that answers 200 with {"error": …} is not succeeding, and a test that says it
        // is has measured the wrong thing.
        await Assert.That(unchecked_.IsError).IsFalse();
        await Assert.That(checked_.IsError).IsTrue();
        await Assert.That(checked_.StatusCode).IsEqualTo("200");
        await Assert.That(checked_.Message).Contains("no error in body");
    }

    [Test]
    public async Task A_body_check_reads_the_body_and_a_status_check_does_not()
    {
        await using var server = TestServer.Start();
        using var client = HttpClientPool.CreateClient();

        var withBody = await client.Send(
            HttpRequest.Get($"{server.BaseAddress}ok").WithBodyCheck("status"), Context());

        await Assert.That(withBody.IsError).IsFalse();
        await Assert.That(HttpCheck.Create("x", (_, _) => true).NeedsBody).IsTrue();
        await Assert.That(HttpCheck.Create("x", _ => true).NeedsBody).IsFalse();
    }

    [Test]
    public async Task A_request_body_goes_out_and_comes_back
        ()
    {
        await using var server = TestServer.Start();
        using var client = HttpClientPool.CreateClient();

        var response = await client.Send(
            HttpRequest.Post($"{server.BaseAddress}echo")
                .WithJsonBody(new { name = "Ada", age = 36 })
                .WithBodyCheck("Ada"),
            Context());

        await Assert.That(response.IsError).IsFalse();
    }

    [Test]
    public async Task Headers_are_sent_and_repeat_rather_than_replace()
    {
        await using var server = TestServer.Start();
        using var client = HttpClientPool.CreateClient();

        var response = await client.Send(
            HttpRequest.Get($"{server.BaseAddress}headers")
                .WithHeader("X-Test", "one")
                .WithBearerToken("token123")
                .WithBodyCheck("X-Test: one")
                .WithCheck(HttpCheck.Create("bearer", (_, body) => body.Contains("Bearer token123"))),
            Context());

        await Assert.That(response.IsError).IsFalse();
    }

    [Test]
    [Category("slow")]
    public async Task A_request_timeout_is_its_own_outcome_rather_than_a_generic_error()
    {
        await using var server = TestServer.Start();
        using var client = HttpClientPool.CreateClient();

        var response = await client.Send(
            HttpRequest.Get($"{server.BaseAddress}slow").WithTimeout(Time.Milliseconds(200)), Context());

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCodes.RequestTimeout);
        await Assert.That(response.Message).Contains("timeout");
    }

    [Test]
    public async Task A_connection_that_never_happens_is_a_transport_error()
    {
        using var client = HttpClientPool.CreateClient();

        // Port 1 on loopback: nothing listens there, and the connect fails immediately.
        var response = await client.Send(HttpRequest.Get("http://127.0.0.1:1/nowhere"), Context());

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCodes.TransportError);
    }

    [Test]
    public async Task A_request_is_a_description_and_can_be_sent_more_than_once()
    {
        await using var server = TestServer.Start();
        using var client = HttpClientPool.CreateClient();

        // The reason this is not an HttpRequestMessage: one of those cannot be sent twice, so
        // a scenario reusing it across iterations would fail on the second.
        var request = HttpRequest.Post($"{server.BaseAddress}echo").WithStringBody("hello");

        for (var i = 0; i < 3; i++)
        {
            var response = await client.Send(request, Context());
            await Assert.That(response.IsError).IsFalse();
        }
    }

    [Test]
    public async Task A_base_address_is_prepended_to_a_relative_url()
    {
        await using var server = TestServer.Start();

        using var client = HttpClientPool.CreateClient(
            HttpClientSettings.Default with { BaseAddress = server.BaseAddress });

        var response = await client.Send(HttpRequest.Get("ok"), Context());

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.StatusCode).IsEqualTo("200");
    }

    [Test]
    public async Task A_bigger_body_reports_a_bigger_size()
    {
        await using var server = TestServer.Start();
        using var client = HttpClientPool.CreateClient();

        var small = await client.Send(HttpRequest.Get($"{server.BaseAddress}ok"), Context());
        var big = await client.Send(HttpRequest.Get($"{server.BaseAddress}big"), Context());

        await Assert.That(big.SizeBytes - small.SizeBytes).IsGreaterThan(9_000);
    }

    [Test]
    public async Task A_client_pool_hands_each_copy_its_own_client()
    {
        using var pool = HttpClientPool.CreatePool(4, HttpClientSettings.Default with { UseCookies = true });

        var first = pool.GetClient(ScenarioInfo(0));
        var second = pool.GetClient(ScenarioInfo(1));

        await Assert.That(pool.Clients.Count).IsEqualTo(4);
        await Assert.That(second).IsNotSameReferenceAs(first);

        // The same copy always gets the same client, which is what makes a cookie jar a session.
        await Assert.That(pool.GetClient(ScenarioInfo(0))).IsSameReferenceAs(first);
        await Assert.That(pool.GetClient(ScenarioInfo(4))).IsSameReferenceAs(first);
    }

    [Test]
    public async Task A_pool_of_no_clients_is_refused()
    {
        await Assert.That(Assert.Throws<AutobahnException>(() => HttpClientPool.CreatePool(0))).IsNotNull();
    }

    private static ScenarioInfo ScenarioInfo(int threadNumber) => new()
    {
        ThreadId = $"copy_{threadNumber}",
        ThreadNumber = threadNumber,
        ScenarioName = "scn",
        ScenarioDuration = TimeSpan.FromSeconds(1),
        CopyCount = 4,
        ScenarioOperation = ScenarioOperation.Bombing
    };

    /// <summary>The bits of a scenario context the HTTP helper actually reads.</summary>
    private sealed class FakeContext : IScenarioContext
    {
        public TestInfo TestInfo => TestInfo.Empty;
        public ScenarioInfo ScenarioInfo => HttpRequestTests.ScenarioInfo(0);
        public HostInfo HostInfo => HostInfo.Empty;
        public Microsoft.Extensions.Logging.ILogger Logger { get; } =
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public Autobahn.Metrics.IMetricRegistry Metrics { get; } =
            new Autobahn.Internal.Domain.Metrics.MetricRegistry();

        public int InvocationNumber => 1;
        public Dictionary<string, object> Data { get; } = [];
        public CancellationToken CancellationToken => CancellationToken.None;

        public void StopScenario(string scenarioName, string reason) { }
        public void StopCurrentTest(string reason) { }
    }
}
