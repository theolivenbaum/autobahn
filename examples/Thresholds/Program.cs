using Autobahn;
using Autobahn.Thresholds;
using static Autobahn.Thresholds.ThresholdComparison;
using static Autobahn.Thresholds.ThresholdSubject;

// A load test that only reports numbers needs a human to read them. Thresholds are what turn
// it into a CI gate: rules checked on every reporting interval and again at the end, with a
// non-zero exit code when one of them fails.

var scenario = Scenario.Create("checkout", async context =>
    {
        // A tenth of the traffic is slow, and a twentieth fails outright.
        var roll = Random.Shared.Next(100);

        await Step.Run("reserve", context, async () =>
        {
            await Task.Delay(roll < 10 ? 300 : 20, context.CancellationToken);
            return Response.Ok(statusCode: "200", sizeBytes: 512);
        });

        return await Step.Run("pay", context, async () =>
        {
            await Task.Delay(30, context.CancellationToken);

            context.Metrics.Counter("payments.attempted").Increment();

            return roll < 5
                ? Response.Fail(statusCode: "500", message: "payment gateway said no")
                : Response.Ok(statusCode: "200", sizeBytes: 256);
        });
    })
    .WithoutWarmUp()
    .WithLoadSimulations(
        Simulation.RampingInject(rate: 40, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(10)),
        Simulation.Inject(rate: 40, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(20)));

var stats = AutobahnRunner
    .RegisterScenarios(scenario)
    .WithTestSuite("examples")
    .WithTestName("thresholds")
    .WithReportingInterval(TimeSpan.FromSeconds(5))
    .WithThresholds(
        // Advisory: recorded, reported, and it fails the run at the end - but the load keeps
        // going, which is what you want from a rule you are still calibrating.
        Threshold.ErrorRateBelow(0.10).Named("checkout stays reliable"),

        // Only checked once the ramp is over, so ramp-up noise does not trip a rule that was
        // written about the steady state.
        Threshold.RpsAbove(30).StartingAfter(TimeSpan.FromSeconds(12)).Named("throughput holds"),

        // Scoped to one step rather than the scenario's totals.
        Threshold.LatencyBelow(Percent99, 250).ForStep("reserve").Named("reserve p99 under 250ms"),

        // Status codes and metrics are threshold subjects too.
        Threshold.Status("500", StatusCodeCount, LessThan, 50).Named("few server errors"),
        // A cumulative claim is about the run, not about each interval of it, so it is checked
        // once at the end - otherwise the ramp's first quiet interval would fail it.
        Threshold.Metric("payments.attempted", MetricCurrent, GreaterThan, 100)
            .OnlyAtTheEnd()
            .Named("the test did something"),

        // With an abort policy. A threshold always states what it *requires*, so a bail-out
        // rule is written the same way as an advisory one - "the error rate stays under half"
        // - and AbortingAfter says how many consecutive violations end the run, rather than
        // carrying on hammering a service that is already down.
        Threshold.ErrorRate(LessThan, 0.5)
            .AbortingAfter(3)
            .Named("bail out if half the traffic is failing"))
    .Run(args);

Console.WriteLine();
Console.WriteLine(stats.AllThresholdsPassed ? "PASS" : "FAIL");

foreach (var threshold in stats.Thresholds)
{
    Console.WriteLine(
        $"  {(threshold.Passed ? "ok  " : "FAIL")} {threshold.Name,-42} "
        + $"observed {threshold.ObservedValue} ({threshold.FailedChecks}/{threshold.TotalChecks} checks failed)");
}

// Autobahn has already set the process exit code to 2 if anything failed, so a CI job that
// runs this binary fails on its own. WithoutThresholdExitCode() opts out of that.
Console.WriteLine();
Console.WriteLine($"exit code: {Environment.ExitCode}");
