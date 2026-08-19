using Autobahn.Internal.Domain;
using Autobahn.Metrics;
using Autobahn.Stats;

namespace Autobahn.Tests;

internal class HintsAnalyzerTests
{
    private static readonly StepStats BaseStepStats = new()
    {
        StepName = "step",
        Ok = MeasurementStats.Empty,
        Fail = MeasurementStats.Empty
    };

    private static readonly ScenarioStats BaseScenarioStats = new()
    {
        ScenarioName = "scenario",
        Ok = MeasurementStats.Empty,
        Fail = MeasurementStats.Empty,
        StepStats = [],
        LoadSimulationStats = new LoadSimulationStats { SimulationName = "", Value = 0 },
        CurrentOperation = OperationType.None,
        AllRequestCount = 0,
        AllOkCount = 0,
        AllFailCount = 0,
        AllBytes = 0,
        Duration = TimeSpan.Zero
    };

    private static SessionStats WithStep(StepStats stepStats) =>
        SessionStats.Empty with { ScenarioStats = [BaseScenarioStats with { StepStats = [stepStats] }] };

    [Test]
    [Arguments(0L, true)]
    [Arguments(1L, false)]
    [Arguments(1024L, false)]
    public async Task A_step_that_tracked_no_data_transfer_gets_a_hint(long minBytes, bool expectHint)
    {
        var ok = MeasurementStats.Empty with
        {
            Request = RequestStats.Empty with { RPS = 1.0 },
            DataTransfer = DataTransferStats.Empty with { MinBytes = minBytes },
            StatusCodes = [new StatusCodeStats { StatusCode = "200", IsError = false, Message = "Success", Count = 1 }]
        };

        var hints = HintsAnalyzer.AnalyzeSessionStats(WithStep(BaseStepStats with { Ok = ok }));

        await Assert.That(hints.Any(x => x.Hint.Contains("didn't track data transfer"))).IsEqualTo(expectHint);
    }

    [Test]
    public async Task A_step_that_tracked_no_status_code_gets_a_hint()
    {
        var ok = MeasurementStats.Empty with
        {
            Request = RequestStats.Empty with { RPS = 1.0, Count = 1 },
            DataTransfer = DataTransferStats.Empty with { MinBytes = 100 },
            StatusCodes = []
        };

        var hints = HintsAnalyzer.AnalyzeSessionStats(WithStep(BaseStepStats with { Ok = ok }));

        await Assert.That(hints.Count).IsGreaterThan(0);
        await Assert.That(hints[0].SourceType).IsEqualTo(HintSourceType.Scenario);
        await Assert.That(hints[0].SourceName).IsEqualTo("scenario");
    }

    [Test]
    public async Task A_fully_instrumented_step_gets_no_hints()
    {
        var ok = MeasurementStats.Empty with
        {
            Request = RequestStats.Empty with { RPS = 1.0, Count = 1 },
            DataTransfer = DataTransferStats.Empty with { MinBytes = 100 },
            StatusCodes = [new StatusCodeStats { StatusCode = "200", IsError = false, Message = "Success", Count = 1 }]
        };

        var hints = HintsAnalyzer.AnalyzeSessionStats(WithStep(BaseStepStats with { Ok = ok }));

        await Assert.That(hints).IsEmpty();
    }
}

[NotInParallel]
public class HintsAnalyzerRunTests
{
    private static ScenarioProps ShortScenario() =>
        Scenario.Create("test", async _ =>
            {
                await Task.Delay(Time.Milliseconds(100));
                return Response.Ok();
            })
            .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(1)))
            .WithoutWarmUp();

    [Test]
    public async Task The_hints_analyzer_is_off_by_default()
    {
        var result = AutobahnRunner.RegisterScenarios(ShortScenario()).WithoutReports().RunWithResult();

        await Assert.That(result.Hints).IsEmpty();
    }

    [Test]
    public async Task The_hints_analyzer_can_be_switched_off_explicitly()
    {
        var result = AutobahnRunner.RegisterScenarios(ShortScenario())
            .EnableHintsAnalyzer(false)
            .WithoutReports()
            .RunWithResult();

        await Assert.That(result.Hints).IsEmpty();
    }

    [Test]
    public async Task The_hints_analyzer_reports_hints_when_it_is_switched_on()
    {
        var scenario = Scenario.Create("test", async ctx =>
            {
                await Step.Run("step", ctx, async () =>
                {
                    await Task.Delay(Time.Milliseconds(50));
                    return Response.Ok();
                });

                return Response.Ok();
            })
            .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(2)))
            .WithoutWarmUp();

        var result = AutobahnRunner.RegisterScenarios(scenario)
            .EnableHintsAnalyzer(true)
            .WithoutReports()
            .RunWithResult();

        await Assert.That(result.Hints).IsNotEmpty();
    }
}

/// <summary>
/// The hints that read the runtime metrics and say the generator, not the target, is what
/// this run measured.
/// </summary>
internal class LoadGeneratorHintTests
{
    private static SessionStats WithMetrics(params MetricStats[] metrics) =>
        SessionStats.Empty with { Metrics = metrics };

    private static MetricStats Gauge(string name, double mean, double max, long count = 10) =>
        MetricStats.Empty(name, MetricKind.Gauge, "count") with { Mean = mean, Max = max, Count = count };

    private static MetricStats Counter(string name, double total) =>
        MetricStats.Empty(name, MetricKind.Counter, "count") with { Current = total, Count = 1 };

    private static string[] Hints(SessionStats stats) =>
        HintsAnalyzer.AnalyzeSessionStats(stats)
            .Where(x => x.SourceType == HintSourceType.LoadGenerator)
            .Select(x => x.Hint)
            .ToArray();

    [Test]
    public async Task A_healthy_run_gets_no_hints_about_the_generator()
    {
        var stats = WithMetrics(
            Gauge(Constants.MetricThreadPoolQueue, mean: 0, max: 3),
            Gauge(Constants.MetricCpuPercent, mean: 22, max: 60),
            Counter(Constants.MetricGen2Collections, 2));

        // A hint that fires on a healthy run gets ignored, and then so does the one that
        // mattered.
        await Assert.That(Hints(stats)).IsEmpty();
    }

    [Test]
    public async Task A_thread_pool_queue_that_never_empties_is_called_out()
    {
        var hints = Hints(WithMetrics(Gauge(Constants.MetricThreadPoolQueue, mean: 84, max: 340)));

        await Assert.That(hints.Length).IsEqualTo(1);
        await Assert.That(hints[0]).Contains("340");
        await Assert.That(hints[0]).Contains("thread-pool queue");
        await Assert.That(hints[0]).Contains(".Result");
    }

    [Test]
    public async Task Sustained_cpu_on_the_generator_is_called_out()
    {
        var hints = Hints(WithMetrics(Gauge(Constants.MetricCpuPercent, mean: 94, max: 99)));

        await Assert.That(hints.Length).IsEqualTo(1);
        await Assert.That(hints[0]).Contains("94");
        await Assert.That(hints[0]).Contains("CPU");
    }

    [Test]
    public async Task A_run_full_of_gen2_collections_is_called_out()
    {
        var hints = Hints(WithMetrics(Counter(Constants.MetricGen2Collections, 40)));

        await Assert.That(hints.Length).IsEqualTo(1);
        await Assert.That(hints[0]).Contains("40 gen2");
        await Assert.That(hints[0]).Contains("allocation");
    }

    [Test]
    public async Task A_metric_that_was_never_written_says_nothing()
    {
        // A run with the runtime metrics turned off must not produce a hint claiming its
        // thread pool was fine, or one claiming it was not.
        var stats = WithMetrics(
            MetricStats.Empty(Constants.MetricThreadPoolQueue, MetricKind.Gauge, "count"),
            MetricStats.Empty(Constants.MetricCpuPercent, MetricKind.Gauge, "%"));

        await Assert.That(Hints(stats)).IsEmpty();
        await Assert.That(Hints(SessionStats.Empty)).IsEmpty();
    }

    [Test]
    public async Task Several_symptoms_produce_several_hints()
    {
        var hints = Hints(WithMetrics(
            Gauge(Constants.MetricThreadPoolQueue, mean: 90, max: 400),
            Gauge(Constants.MetricCpuPercent, mean: 97, max: 100),
            Counter(Constants.MetricGen2Collections, 60)));

        await Assert.That(hints.Length).IsEqualTo(3);
    }
}
