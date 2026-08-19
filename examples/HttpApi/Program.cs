using Autobahn;
using Autobahn.Feeds;
using Autobahn.Http;
using Autobahn.OpenTelemetry;
using Autobahn.Thresholds;
using static Autobahn.Thresholds.ThresholdSubject;

// Hammering an HTTP API: a request builder, checks that decide what "success" means, feeds
// for the data, thresholds for the verdict, and OTLP so the numbers land wherever the rest of
// your telemetry does.
//
// Points at httpbin.org by default. Override with the first argument, or an
// AUTOBAHN_TARGET_HOST environment variable, to run it against something of your own.

var host = args.FirstOrDefault(x => !x.StartsWith('-'))
           ?? Environment.GetEnvironmentVariable("AUTOBAHN_TARGET_HOST")
           ?? "https://httpbin.org";

// The clients. One per virtual user, each with its own cookie jar, because each copy is
// meant to be a distinct session - sharing one client would make them one user with
// twenty times the traffic.
using var clients = HttpClientPool.CreatePool(
    count: 20,
    new HttpClientSettings
    {
        BaseAddress = host,
        Timeout = TimeSpan.FromSeconds(30),
        UseCookies = true
    });

// The data. Circular, so every id is used before any is reused.
var userIds = Feed.Circular("user_ids", Enumerable.Range(1, 500).ToArray());

var read = Scenario.Create("read", async context =>
    {
        var client = clients.GetClient(context.ScenarioInfo);

        var response = await client.Send(
            HttpRequest.Get($"/anything/users/{userIds.Next()}")
                .WithHeader("Accept", "application/json")
                // Without a check, a 2xx is a success. With one, a 2xx that fails it is not -
                // which is the point: an API answering 200 with {"error": …} is not succeeding.
                .WithCheck(HttpCheck.Create("no error in body", (_, body) => !body.Contains("\"error\"")))
                .WithTimeout(TimeSpan.FromSeconds(5)),
            context);

        return response;
    })
    .WithWeight(80)
    .WithoutWarmUp()
    .WithLoadSimulations(
        Simulation.RampingInject(rate: 20, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(10)),
        Simulation.Inject(rate: 20, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(20)));

var write = Scenario.Create("write", async context =>
    {
        var client = clients.GetClient(context.ScenarioInfo);

        context.Metrics.Counter("orders.placed").Increment();

        return await client.Send(
            HttpRequest.Post("/anything/orders")
                .WithJsonBody(new { userId = userIds.Next(), quantity = 2 })
                .WithStatusCheck(200)
                .WithTimeout(TimeSpan.FromSeconds(5)),
            context);
    })
    .WithWeight(20)
    .WithoutWarmUp()
    .WithLoadSimulations(
        Simulation.RampingInject(rate: 20, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(10)),
        Simulation.Inject(rate: 20, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(20)));

var context = AutobahnRunner
    .RegisterScenarios(read, write)
    .WithTestSuite("examples")
    .WithTestName("http api")
    .WithThresholds(
        Threshold.ErrorRateBelow(0.05).Named("the api stays up"),
        Threshold.LatencyBelow(Percent99, 3_000).Named("p99 under three seconds"));

// Exporting is opt-in and takes one call. Nothing is sent unless a collector is configured,
// which is what OTEL_EXPORTER_OTLP_ENDPOINT already means everywhere else.
if (Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") is { Length: > 0 })
{
    context = context.WithOpenTelemetry(out var exporter, new AutobahnOtlpOptions
    {
        ServiceName = "autobahn-http-example",
        ResourceAttributes = new Dictionary<string, object> { ["deployment.environment"] = "local" }
    });

    // Disposed after the run so the last interval is flushed rather than lost on exit.
    using (exporter) Report(context.Run(args));
}
else
{
    Console.WriteLine("Set OTEL_EXPORTER_OTLP_ENDPOINT to also push these numbers to a collector.");
    Console.WriteLine();

    Report(context.Run(args));
}

static void Report(Autobahn.Stats.SessionStats stats)
{
    Console.WriteLine();
    Console.WriteLine(stats.AllThresholdsPassed ? "PASS" : "FAIL");

    foreach (var scn in stats.ScenarioStats)
    {
        Console.WriteLine(
            $"  {scn.ScenarioName}: ok {scn.Ok.Request.Count}, fail {scn.Fail.Request.Count}, "
            + $"p99 {scn.Ok.Latency.Percent99} ms");
    }
}
