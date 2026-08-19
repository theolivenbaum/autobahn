using Autobahn;

// Everything about *how much* work a run does, and *which* work each copy picks up.
//
//   - weights, so two scenarios split one traffic mix instead of being rated by hand
//   - counted simulations, so a run is N iterations rather than N seconds
//   - instance-aware partitioning, so every copy owns a disjoint slice of the data
//   - a Zipfian distribution, so the keys are hot-skewed the way real traffic is
//   - timeouts, a completion hook, and a cancellation token that ends the run early

// The dataset every copy shares. Each copy takes a disjoint stride through it.
var catalogue = Enumerable.Range(1, 5_000).Select(i => $"sku-{i:D5}").ToArray();

// Zipfian: a small hot minority of keys gets most of the traffic, which is what a cache
// or a content service actually sees. Uniform() and Multinomial() are the alternatives.
var popularity = Distribution.Zipfian(catalogue, skew: 1.1);

var browse = Scenario.Create("browse", async context =>
    {
        // The copy's own slice of the catalogue: copy 3 of 20 walks rows 3, 23, 43…
        // Partition() hands back the whole slice; ItemForIteration() walks it one row per
        // iteration, which is the common case. Neither picks a row another copy also owns.
        var sku = context.ItemForIteration(catalogue) ?? popularity.Next();

        await Task.Delay(Random.Shared.Next(10, 40), context.CancellationToken);

        return Response.Ok(statusCode: "200", sizeBytes: sku.Length);
    })
    // 80% of the mix. The weights are applied to the combined load model below, so the
    // rates written on the simulations are the *total* the two scenarios share.
    .WithWeight(80)
    .WithoutWarmUp()
    // A slow iteration is recorded as a timeout ("-102") rather than as a generic error,
    // so the report distinguishes "slow" from "broken".
    .WithIterationTimeout(TimeSpan.FromSeconds(2))
    // When the plan ends, in-flight iterations get this long to finish and be counted.
    .WithCompletionTimeout(TimeSpan.FromSeconds(5))
    .WithLoadSimulations(
        Simulation.RampingInject(rate: 100, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(5)),
        Simulation.Inject(rate: 100, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(10)));

var checkout = Scenario.Create("checkout", async context =>
    {
        // A step is a measured sub-part of an iteration, and can carry its own timeout.
        await Step.Run("reserve", context,
            () => Task.Delay(20).ContinueWith(_ => Response.Ok()),
            timeout: TimeSpan.FromSeconds(1));

        return await Step.Run("pay", context,
            () => Task.Delay(30).ContinueWith(_ => Response.Ok()));
    })
    // The remaining 20%.
    .WithWeight(20)
    .WithoutWarmUp()
    // Fires when this scenario finishes, with its own final stats in hand - the place to
    // push a result somewhere or fail a build without wrapping the whole runner.
    .WithCompletionHook(ctx =>
    {
        Console.WriteLine($"[hook] {ctx.ScenarioInfo.ScenarioName}: "
                          + $"{ctx.Stats.Ok.Request.Count} ok, p99 {ctx.Stats.Ok.Latency.Percent99} ms");
        return Task.CompletedTask;
    })
    .WithLoadSimulations(
        Simulation.RampingInject(rate: 100, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(5)),
        Simulation.Inject(rate: 100, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(10)));

// Cancelling ends the run early and still writes the reports. Ctrl+C does the same thing
// without any wiring; pass WithoutCancelKeyPress() to leave Ctrl+C to the runtime.
using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2));

var weighted = AutobahnRunner
    .RegisterScenarios(browse, checkout)
    .WithTestSuite("examples")
    .WithTestName("weighted mix")
    .WithCancellationToken(cancel.Token)
    .Run(args);

foreach (var scn in weighted.ScenarioStats)
    Console.WriteLine($"{scn.ScenarioName}: ok {scn.Ok.Request.Count}, fail {scn.Fail.Request.Count}");

// A weight is a share of a total, so it is all-or-nothing: a run where only some scenarios
// declare one has no total to split, and Autobahn rejects it by name. The counted scenario
// below therefore gets a run of its own.

// A counted simulation runs an exact number of iterations rather than for a duration, which
// is what makes a load test usable as a smoke test: 200 iterations, four copies, no clock.
var smoke = Scenario.Create("smoke", async context =>
    {
        await Task.Delay(5, context.CancellationToken);
        return Response.Ok();
    })
    .WithoutWarmUp()
    .WithLoadSimulations(Simulation.IterationsForConstant(copies: 4, iterations: 200));

var counted = AutobahnRunner
    .RegisterScenarios(smoke)
    .WithTestSuite("examples")
    .WithTestName("smoke")
    .Run(args);

Console.WriteLine($"smoke: {counted.AllRequestCount} iterations (asked for exactly 200)");
