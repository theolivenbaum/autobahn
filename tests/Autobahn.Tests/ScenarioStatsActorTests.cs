using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Stats;
using Microsoft.Extensions.Logging.Abstractions;

namespace Autobahn.Tests;

internal class ScenarioStatsActorTests
{
    private static readonly LoadSimulationStats LoadStats = new() { SimulationName = "", Value = 10 };

    private static RuntimeScenario BaseScenario() =>
        ScenarioFactory.CreateScenario(Scenario.Create("scenario", async ctx =>
        {
            await Task.Delay(Time.Seconds(0.5));
            return Response.Ok();
        })).Value;

    private static ScenarioStatsActor CreateActor() =>
        new(NullLogger.Instance, BaseScenario(), Time.Seconds(5));

    private static Measurement Measure(string name, IResponse response, TimeSpan timeBucket, TimeSpan latency) =>
        new(name, response, timeBucket, latency);

    [Test]
    public async Task Interval_stats_are_cached_by_duration()
    {
        await using var statsActor = CreateActor();

        for (var i = 1; i <= 10; i++)
        {
            statsActor.AddMeasurement(Measure("step_name", Response.Ok(), TimeSpan.Zero, Time.Seconds(i)));
            statsActor.AddMeasurement(Measure(Constants.ScenarioGlobalInfo, Response.Ok(), TimeSpan.Zero, Time.Seconds(i)));
        }

        var duration = Time.Seconds(10);
        var realtimeStats = await statsActor.BuildReportingStats(LoadStats, duration);

        await Assert.That(statsActor.AllRealtimeStats[duration].Ok.Request.Count).IsEqualTo(10);
        await Assert.That(statsActor.AllRealtimeStats[duration].StepStats[0].Ok.Request.Count).IsEqualTo(10);
        await Assert.That(statsActor.AllRealtimeStats[duration].StepStats[0].Fail.Request.Count).IsEqualTo(0);
        await Assert.That(realtimeStats.Ok.Request.Count).IsEqualTo(10);
    }

    [Test]
    public async Task A_measurement_from_a_later_interval_waits_for_that_interval()
    {
        await using var statsActor = CreateActor();

        // Belongs to the interval that is open now.
        for (var i = 1; i <= 5; i++)
        {
            statsActor.AddMeasurement(Measure("step_name", Response.Ok(), Time.Seconds(1), Time.Seconds(i)));
            statsActor.AddMeasurement(Measure(Constants.ScenarioGlobalInfo, Response.Ok(), Time.Seconds(1), Time.Seconds(i)));
        }

        // Belongs to the interval after it, and must not be counted in this one.
        for (var i = 1; i <= 10; i++)
        {
            statsActor.AddMeasurement(Measure("step_name", Response.Ok(), Time.Seconds(6), Time.Seconds(i)));
            statsActor.AddMeasurement(Measure(Constants.ScenarioGlobalInfo, Response.Ok(), Time.Seconds(6), Time.Seconds(i)));
        }

        var fiveSec = Time.Seconds(5);
        var tenSec = Time.Seconds(10);

        await statsActor.BuildReportingStats(LoadStats, fiveSec);
        await statsActor.BuildReportingStats(LoadStats, tenSec);

        await Assert.That(statsActor.AllRealtimeStats[fiveSec].Ok.Request.Count).IsEqualTo(5);
        await Assert.That(statsActor.AllRealtimeStats[tenSec].Ok.Request.Count).IsEqualTo(10);
    }

    [Test]
    public async Task Steps_are_reported_in_the_order_they_first_arrived()
    {
        await using var statsActor = CreateActor();

        string[] stepNames = ["zulu", "alpha", "mike", "bravo", "yankee"];

        foreach (var stepName in stepNames)
            statsActor.AddMeasurement(Measure(stepName, Response.Ok(), TimeSpan.Zero, Time.Seconds(100)));

        statsActor.AddMeasurement(Measure(Constants.ScenarioGlobalInfo, Response.Ok(), TimeSpan.Zero, Time.Seconds(100)));

        var scnStats = await statsActor.BuildReportingStats(
            new LoadSimulationStats { SimulationName = "", Value = 9 }, Time.Seconds(10));

        for (var i = 0; i < stepNames.Length; i++)
            await Assert.That(scnStats.StepStats[i].StepName).IsEqualTo(stepNames[i]);
    }

    [Test]
    public async Task The_scenario_row_accounts_for_the_bytes_its_steps_transferred()
    {
        await using var statsActor = CreateActor();
        var simulation = new LoadSimulationStats { SimulationName = "", Value = 9 };

        for (var i = 1; i <= 100; i++)
        {
            statsActor.AddMeasurement(Measure(
                "step_small", Response.Ok(sizeBytes: 1), TimeSpan.Zero, Time.Seconds(100)));
        }

        statsActor.AddMeasurement(Measure("step_big", Response.Ok(sizeBytes: 1000), TimeSpan.Zero, Time.Seconds(100)));

        statsActor.AddMeasurement(Measure(
            Constants.ScenarioGlobalInfo, Response.Ok(sizeBytes: 5), TimeSpan.Zero, Time.Seconds(100)));

        var scnStats = await statsActor.BuildReportingStats(simulation, Time.Seconds(10));

        // The accumulated step bytes were consumed by the scenario row, so the next interval
        // starts from nothing again.
        statsActor.AddMeasurement(Measure("step_big", Response.Ok(sizeBytes: 1000), TimeSpan.Zero, Time.Seconds(100)));
        var scnStats2 = await statsActor.BuildReportingStats(simulation, Time.Seconds(10));

        // 100 (step_small) + 1000 (step_big) + 5 (the scenario's own bytes)
        await Assert.That(scnStats.AllBytes).IsEqualTo(1105L);
        await Assert.That(scnStats.Ok.DataTransfer.AllBytes).IsEqualTo(1105L);
        await Assert.That(scnStats.Fail.DataTransfer.AllBytes).IsEqualTo(0L);
        await Assert.That(scnStats.Ok.DataTransfer.Percent99).IsEqualTo(1105L);
        await Assert.That(scnStats.GetStepStats("step_small").Ok.DataTransfer.AllBytes).IsEqualTo(100L);
        await Assert.That(scnStats.GetStepStats("step_big").Ok.DataTransfer.AllBytes).IsEqualTo(1000L);

        await Assert.That(scnStats2.Ok.DataTransfer.AllBytes).IsEqualTo(0L);
        await Assert.That(scnStats2.AllBytes).IsEqualTo(1000L);
    }

    [Test]
    public async Task A_failed_scenario_iteration_counts_towards_the_fail_count()
    {
        await using var statsActor = CreateActor();

        for (var i = 0; i < 7; i++)
            statsActor.AddMeasurement(Measure(Constants.ScenarioGlobalInfo, Response.Fail(), TimeSpan.Zero, Time.Seconds(1)));

        // A failed step is not a failed iteration.
        statsActor.AddMeasurement(Measure("step", Response.Fail(), TimeSpan.Zero, Time.Seconds(1)));

        await statsActor.BuildReportingStats(LoadStats, Time.Seconds(5));

        await Assert.That(statsActor.ScenarioFailCount).IsEqualTo(7);
    }

    [Test]
    public async Task The_console_table_accumulates_absolute_counts_across_intervals()
    {
        await using var statsActor = CreateActor();

        statsActor.AddMeasurement(Measure(Constants.ScenarioGlobalInfo, Response.Ok(), TimeSpan.Zero, Time.Seconds(1)));
        await statsActor.BuildReportingStats(LoadStats, Time.Seconds(5));

        statsActor.AddMeasurement(Measure(Constants.ScenarioGlobalInfo, Response.Ok(), Time.Seconds(5), Time.Seconds(1)));
        await statsActor.BuildReportingStats(LoadStats, Time.Seconds(10));

        var globalInfoRow = statsActor.ConsoleScenarioStats.StepStats
            .First(x => x.StepName == Constants.ScenarioGlobalInfo);

        await Assert.That(globalInfoRow.Ok.Request.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Final_stats_cover_the_whole_run_rather_than_one_interval()
    {
        await using var statsActor = CreateActor();

        statsActor.AddMeasurement(Measure(Constants.ScenarioGlobalInfo, Response.Ok(), TimeSpan.Zero, Time.Seconds(1)));
        await statsActor.BuildReportingStats(LoadStats, Time.Seconds(5));

        statsActor.AddMeasurement(Measure(Constants.ScenarioGlobalInfo, Response.Ok(), Time.Seconds(5), Time.Seconds(1)));
        await statsActor.BuildReportingStats(LoadStats, Time.Seconds(10));

        var finalStats = await statsActor.GetFinalStats(LoadStats, Time.Seconds(10), TimeSpan.Zero);

        await Assert.That(finalStats.Ok.Request.Count).IsEqualTo(2);
        await Assert.That(finalStats.CurrentOperation).IsEqualTo(OperationType.Complete);
    }
}
