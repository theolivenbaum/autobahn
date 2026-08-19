using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Stats;

namespace Autobahn.Tests;

/// <summary>
/// The pure statistics maths.
/// </summary>
/// <remarks>
/// The fork point drove these from FsCheck. They are expressed here as explicit cases plus a
/// seeded random sweep: the invariants are arithmetic, so a fixed set of representative values
/// and a few hundred generated ones cover them without the test project depending on an F#
/// property library - which is exactly the dependency the port exists to shed.
/// </remarks>
internal class RawMeasurementStatsTests
{
    private static Measurement Measure(bool isError, double clientLatencyMs, double measuredMs, long sizeBytes) =>
        new("step_name",
            isError
                ? Response.Fail(sizeBytes: sizeBytes, latencyMs: clientLatencyMs)
                : Response.Ok(sizeBytes: sizeBytes, latencyMs: clientLatencyMs),
            TimeSpan.Zero,
            Time.Milliseconds(measuredMs));

    [Test]
    [Arguments(0.0, 42.0, 42.0)]
    [Arguments(17.0, 42.0, 17.0)]
    [Arguments(900.0, 42.0, 900.0)]
    public async Task A_client_measured_latency_wins_over_the_one_Autobahn_timed(
        double clientLatencyMs, double measuredMs, double expectedMs)
    {
        var data = RawMeasurementStats.Empty("step_name");
        data.AddMeasurement(Measure(isError: false, clientLatencyMs, measuredMs, sizeBytes: 10), 10);

        var min = Converter.FromMicroSecToMs(data.OkStats.MinMicroSec);

        await Assert.That(min).IsEqualTo(expectedMs);
    }

    [Test]
    public async Task Latencies_land_in_the_right_band()
    {
        var random = new Random(20260819);
        var data = RawMeasurementStats.Empty("step_name");

        var latencies = Enumerable.Range(0, 500).Select(_ => (double)random.Next(1, 2000)).ToArray();

        foreach (var latency in latencies)
            data.AddMeasurement(Measure(isError: false, latency, 0, sizeBytes: 10), 10);

        await Assert.That(data.OkStats.LessOrEq800).IsEqualTo(latencies.Count(x => x <= 800.0));
        await Assert.That(data.OkStats.More800Less1200).IsEqualTo(latencies.Count(x => x is > 800.0 and < 1200.0));
        await Assert.That(data.OkStats.MoreOrEq1200).IsEqualTo(latencies.Count(x => x >= 1200.0));
    }

    [Test]
    public async Task Ok_and_fail_measurements_are_tallied_separately()
    {
        var random = new Random(20260820);
        var data = RawMeasurementStats.Empty("step_name");

        var measurements = Enumerable.Range(0, 500)
            .Select(_ => (IsOk: random.Next(2) == 0, Latency: (double)random.Next(1, 2000)))
            .ToArray();

        foreach (var (isOk, latency) in measurements)
            data.AddMeasurement(Measure(!isOk, clientLatencyMs: 0, measuredMs: latency, sizeBytes: 10), 10);

        var ok = measurements.Where(x => x.IsOk).Select(x => x.Latency).ToArray();
        var fail = measurements.Where(x => !x.IsOk).Select(x => x.Latency).ToArray();

        await Assert.That(data.OkStats.RequestCount).IsEqualTo(ok.Length);
        await Assert.That(data.OkStats.LessOrEq800).IsEqualTo(ok.Count(x => x <= 800.0));
        await Assert.That(Converter.FromMicroSecToMs(data.OkStats.MinMicroSec)).IsEqualTo(ok.Min());
        await Assert.That(Converter.FromMicroSecToMs(data.OkStats.MaxMicroSec)).IsEqualTo(ok.Max());

        await Assert.That(data.FailStats.RequestCount).IsEqualTo(fail.Length);
        await Assert.That(data.FailStats.LessOrEq800).IsEqualTo(fail.Count(x => x <= 800.0));
        await Assert.That(Converter.FromMicroSecToMs(data.FailStats.MinMicroSec)).IsEqualTo(fail.Min());
        await Assert.That(Converter.FromMicroSecToMs(data.FailStats.MaxMicroSec)).IsEqualTo(fail.Max());
    }

    [Test]
    public async Task Response_sizes_are_tallied_separately_for_ok_and_fail()
    {
        var random = new Random(20260821);
        var data = RawMeasurementStats.Empty("step");

        var responses = Enumerable.Range(0, 400)
            .Select(_ => (IsOk: random.Next(2) == 0, Size: (long)random.Next(1, 5000)))
            .ToArray();

        foreach (var (isOk, size) in responses)
            data.AddMeasurement(Measure(!isOk, clientLatencyMs: 1.0, measuredMs: 0, sizeBytes: size), size);

        var ok = responses.Where(x => x.IsOk).Select(x => x.Size).ToArray();
        var fail = responses.Where(x => !x.IsOk).Select(x => x.Size).ToArray();

        await Assert.That(data.OkStats.RequestCount).IsEqualTo(ok.Length);
        await Assert.That(data.OkStats.MinBytes).IsEqualTo(ok.Min());
        await Assert.That(data.OkStats.MaxBytes).IsEqualTo(ok.Max());
        await Assert.That(data.OkStats.AllBytes).IsEqualTo(ok.Sum());

        await Assert.That(data.FailStats.RequestCount).IsEqualTo(fail.Length);
        await Assert.That(data.FailStats.MinBytes).IsEqualTo(fail.Min());
        await Assert.That(data.FailStats.MaxBytes).IsEqualTo(fail.Max());
        await Assert.That(data.FailStats.AllBytes).IsEqualTo(fail.Sum());
    }

    [Test]
    public async Task A_measurement_with_no_latency_counts_but_records_no_distribution()
    {
        var data = RawMeasurementStats.Empty("step");
        data.AddMeasurement(Measure(isError: false, clientLatencyMs: 0, measuredMs: 0, sizeBytes: 100), 100);

        await Assert.That(data.OkStats.RequestCount).IsEqualTo(1);
        await Assert.That(data.OkStats.LatencyHistogram.TotalCount).IsEqualTo(0L);
        await Assert.That(data.OkStats.AllBytes).IsEqualTo(0L);
    }
}

internal class ScenarioStatsCalculationTests
{
    private static readonly LoadSimulationStats BaseSimulation = new() { SimulationName = "simulation name", Value = 1 };

    private static ScenarioStats Create(params RawMeasurementStats[] rawStats) =>
        Statistics.CreateScenarioStats(
            "scenario", rawStats, BaseSimulation, OperationType.Complete,
            Time.Seconds(1), Time.Seconds(1), TimeSpan.Zero);

    private static RawMeasurementStats WithCounts(string name, int okCount, int failCount)
    {
        var stats = RawMeasurementStats.Empty(name);
        stats.OkStats.RequestCount = okCount;
        stats.FailStats.RequestCount = failCount;
        return stats;
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(10, 0)]
    [Arguments(0, 10)]
    [Arguments(1234, 4321)]
    public async Task Request_count_is_ok_plus_fail(int okCount, int failCount)
    {
        var stats = Create(WithCounts(Constants.ScenarioGlobalInfo, okCount, failCount));

        await Assert.That(stats.Ok.Request.Count + stats.Fail.Request.Count).IsEqualTo(okCount + failCount);
    }

    [Test]
    [Arguments(0L, 0L)]
    [Arguments(1_000L, 2_000L)]
    [Arguments(3_000_000_000L, 1L)]
    public async Task All_bytes_is_the_sum_of_ok_and_fail_bytes(long okAllBytes, long failAllBytes)
    {
        var raw = RawMeasurementStats.Empty(Constants.ScenarioGlobalInfo);
        raw.OkStats.AllBytes = okAllBytes;
        raw.FailStats.AllBytes = failAllBytes;

        var stats = Create(raw);

        await Assert.That(Statistics.CalcAllBytes(stats)).IsEqualTo(okAllBytes + failAllBytes);
    }

    [Test]
    [Arguments(7, 13)]
    [Arguments(0, 5)]
    public async Task The_scenario_row_is_separate_from_its_steps(int scenarioCount, int stepCount)
    {
        var stats = Create(
            WithCounts(Constants.ScenarioGlobalInfo, scenarioCount, 0),
            WithCounts("step", stepCount, 0));

        await Assert.That(stats.Ok.Request.Count).IsEqualTo(scenarioCount);
        await Assert.That(stats.StepStats[0].Ok.Request.Count).IsEqualTo(stepCount);
    }

    [Test]
    [Arguments(7, 13)]
    [Arguments(0, 5)]
    public async Task The_scenario_fail_row_is_separate_from_its_steps(int scenarioCount, int stepCount)
    {
        var stats = Create(
            WithCounts(Constants.ScenarioGlobalInfo, 0, scenarioCount),
            WithCounts("step", 0, stepCount));

        await Assert.That(stats.Fail.Request.Count).IsEqualTo(scenarioCount);
        await Assert.That(stats.StepStats[0].Fail.Request.Count).IsEqualTo(stepCount);
    }

    [Test]
    [Arguments(1, 2, 3)]
    [Arguments(0, 0, 0)]
    public async Task Latency_bands_carry_through_to_the_scenario_row(int less800, int more800Less1200, int more1200)
    {
        var raw = RawMeasurementStats.Empty(Constants.ScenarioGlobalInfo);

        foreach (var side in new[] { raw.OkStats, raw.FailStats })
        {
            side.LessOrEq800 = less800;
            side.More800Less1200 = more800Less1200;
            side.MoreOrEq1200 = more1200;
        }

        var stats = Create(raw);

        await Assert.That(stats.Ok.Latency.LatencyCount.LessOrEq800).IsEqualTo(less800);
        await Assert.That(stats.Ok.Latency.LatencyCount.More800Less1200).IsEqualTo(more800Less1200);
        await Assert.That(stats.Ok.Latency.LatencyCount.MoreOrEq1200).IsEqualTo(more1200);

        await Assert.That(stats.Fail.Latency.LatencyCount.LessOrEq800).IsEqualTo(less800);
        await Assert.That(stats.Fail.Latency.LatencyCount.More800Less1200).IsEqualTo(more800Less1200);
        await Assert.That(stats.Fail.Latency.LatencyCount.MoreOrEq1200).IsEqualTo(more1200);
    }
}

public class SessionStatsCalculationTests
{
    private static readonly TestInfo BaseTestInfo = new()
    {
        SessionId = "session id", TestSuite = "test suite", TestName = "test name"
    };

    private static readonly HostInfo BaseHostInfo = HostInfo.Empty with
    {
        MachineName = "machine name",
        CurrentOperation = OperationType.Complete,
        CoresCount = 4,
        AutobahnVersion = "0.1.0"
    };

    private static readonly ScenarioStats BaseScenarioStats = new()
    {
        ScenarioName = "scenario name",
        Ok = MeasurementStats.Empty,
        Fail = MeasurementStats.Empty,
        StepStats = [],
        LoadSimulationStats = new LoadSimulationStats { SimulationName = "simulation name", Value = 1 },
        CurrentOperation = OperationType.Complete,
        AllRequestCount = 0,
        AllOkCount = 0,
        AllFailCount = 0,
        AllBytes = 0,
        Duration = TimeSpan.Zero
    };

    [Test]
    [Arguments(0, 0, 0L)]
    [Arguments(10, 5, 1_000L)]
    [Arguments(999, 1, 3_000_000_000L)]
    public async Task Session_totals_are_the_sum_of_their_scenarios(int okCount, int failCount, long allBytes)
    {
        var scenario1 = BaseScenarioStats with { AllRequestCount = okCount, AllOkCount = okCount, AllBytes = allBytes };
        var scenario2 = BaseScenarioStats with { AllRequestCount = failCount, AllFailCount = failCount, AllBytes = allBytes };

        var sessionStats = Statistics.CreateSessionStats(BaseTestInfo, BaseHostInfo, [scenario1, scenario2]);

        await Assert.That(sessionStats.AllRequestCount).IsEqualTo(okCount + failCount);
        await Assert.That(sessionStats.AllOkCount).IsEqualTo(okCount);
        await Assert.That(sessionStats.AllFailCount).IsEqualTo(failCount);
        await Assert.That(sessionStats.AllBytes).IsEqualTo(allBytes + allBytes);
    }

    [Test]
    public async Task Session_duration_is_the_longest_scenario()
    {
        var scenario1 = BaseScenarioStats with { Duration = Time.Seconds(10) };
        var scenario2 = BaseScenarioStats with { Duration = Time.Seconds(20) };

        var sessionStats = Statistics.CreateSessionStats(BaseTestInfo, BaseHostInfo, [scenario1, scenario2]);

        await Assert.That(sessionStats.Duration).IsEqualTo(Time.Seconds(20));
    }

    [Test]
    public async Task A_session_with_no_scenarios_still_carries_its_identity()
    {
        var sessionStats = Statistics.CreateSessionStats(BaseTestInfo, BaseHostInfo, []);

        await Assert.That(sessionStats.TestInfo).IsEqualTo(BaseTestInfo);
        await Assert.That(sessionStats.HostInfo).IsEqualTo(BaseHostInfo);
        await Assert.That(sessionStats.ScenarioStats).IsEmpty();
    }

    [Test]
    public async Task Merged_status_codes_come_back_sorted()
    {
        StatusCodeStats[] stats =
        [
            new() { StatusCode = "50", IsError = false, Message = "", Count = 1 },
            new() { StatusCode = "80", IsError = false, Message = "", Count = 1 },
            new() { StatusCode = "10", IsError = false, Message = "", Count = 1 }
        ];

        var result = Statistics.MergeStatusCodes(stats).Select(x => x.StatusCode).ToArray();

        await Assert.That(result).IsEquivalentTo(new[] { "10", "50", "80" });
    }

    [Test]
    public async Task Merged_status_codes_sum_their_counts()
    {
        StatusCodeStats[] stats =
        [
            new() { StatusCode = "200", IsError = false, Message = "ok", Count = 3 },
            new() { StatusCode = "200", IsError = false, Message = "ok", Count = 4 }
        ];

        var result = Statistics.MergeStatusCodes(stats);

        await Assert.That(result.Length).IsEqualTo(1);
        await Assert.That(result[0].Count).IsEqualTo(7);
    }

    [Test]
    [Arguments(0, 1.0, 0.0)]
    [Arguments(10, 1.0, 10.0)]
    [Arguments(10, 0.5, 10.0)]  // windows under a second are treated as a second
    [Arguments(100, 10.0, 10.0)]
    public async Task Rps_is_requests_over_the_executed_window(int requestCount, double seconds, double expected)
    {
        var rps = Statistics.CalcRps(TimeSpan.FromSeconds(seconds), requestCount);

        await Assert.That(rps).IsEqualTo(expected);
    }
}
