using Autobahn.Internal.Domain;
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
