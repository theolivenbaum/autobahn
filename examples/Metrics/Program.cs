using Autobahn;
using Autobahn.Metrics;

// Latency, throughput and status codes describe the *target*. Metrics describe everything
// else: the queue you are draining, the cache you are missing, and the load generator's own
// health - CPU, memory, GC, thread pool, sockets - which Autobahn collects on its own.

// A metric is registered by name. Asking twice hands back the same one, so a scenario can
// take its counter in Init and use it on the hot path without either having to be first.
ICounter cacheHits = null!;
ICounter cacheMisses = null!;
IGauge queueDepth = null!;
IHistogram payloadSize = null!;

var queue = 0;

var scenario = Scenario.Create("api", async context =>
    {
        // A counter write is one interlocked add, so this is cheap enough for the hot path.
        if (context.InvocationNumber % 4 == 0) cacheMisses.Increment();
        else cacheHits.Increment();

        // A gauge is the current value of something. Last write wins; the report shows how
        // it moved over the run.
        queueDepth.Set(Interlocked.Increment(ref queue));

        await Task.Delay(Random.Shared.Next(5, 25), context.CancellationToken);

        // A histogram is a distribution, reported with percentiles - the same HdrHistogram
        // the latency numbers use, so p99 means the same thing in both places.
        var bytes = Random.Shared.Next(500, 20_000);
        payloadSize.Record(bytes);

        Interlocked.Decrement(ref queue);

        return Response.Ok(statusCode: "200", sizeBytes: bytes);
    })
    .WithInit(context =>
    {
        // MetricUnit says how a raw value is displayed: record bytes, report kilobytes.
        cacheHits = context.Metrics.Counter("cache.hit");
        cacheMisses = context.Metrics.Counter("cache.miss");
        queueDepth = context.Metrics.Gauge("queue.depth", MetricUnit.Count);
        payloadSize = context.Metrics.Histogram("payload.size", MetricUnit.Kilobytes);

        return Task.CompletedTask;
    })
    .WithoutWarmUp()
    .WithLoadSimulations(
        Simulation.RampingInject(rate: 60, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(10)),
        Simulation.Inject(rate: 60, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(20)));

var stats = AutobahnRunner
    .RegisterScenarios(scenario)
    .WithTestSuite("examples")
    .WithTestName("metrics")
    // The runtime metrics are on by default; WithoutRuntimeMetrics() turns them off.
    .Run(args);

Console.WriteLine();

foreach (var metric in stats.Metrics)
{
    var unit = string.IsNullOrEmpty(metric.Unit) ? "" : $" {metric.Unit}";
    Console.WriteLine($"{metric.Name,-32} {metric.Kind,-9} current {metric.Current}{unit}");
}

var hits = stats.Metrics.Single(x => x.Name == "cache.hit").Current;
var misses = stats.Metrics.Single(x => x.Name == "cache.miss").Current;

Console.WriteLine();
Console.WriteLine($"cache hit ratio: {hits / (hits + misses):P1}");
