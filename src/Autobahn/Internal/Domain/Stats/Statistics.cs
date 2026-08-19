using HdrHistogram;
using Autobahn.Stats;

namespace Autobahn.Internal.Domain.Stats;

/// <summary>Turns raw tallies into the stats records the console, the reports and the API read.</summary>
internal static class Statistics
{
    private static double MicroSecToMs(double microSec) =>
        Converter.Round(Converter.FromMicroSecToMs(microSec), Constants.StatsRounding);

    public static double CalcRps(TimeSpan executionTime, int requestCount)
    {
        var totalSec = executionTime.TotalSeconds < 1.0 ? 1.0 : executionTime.TotalSeconds;
        return requestCount / totalSec;
    }

    public static RequestStats CreateRequestStats(RawItemStats stats, TimeSpan executionTime) => new()
    {
        Count = stats.RequestCount,
        RPS = Converter.Round(CalcRps(executionTime, stats.RequestCount), 1)
    };

    public static LatencyStats CreateLatencyStats(RawItemStats stats)
    {
        var histogram = stats.LatencyHistogram;
        var hasLatencies = histogram.TotalCount > 0;

        return new LatencyStats
        {
            MinMs = hasLatencies ? MicroSecToMs(stats.MinMicroSec) : 0.0,
            MeanMs = hasLatencies ? MicroSecToMs(histogram.GetMean()) : 0.0,
            MaxMs = hasLatencies ? MicroSecToMs(stats.MaxMicroSec) : 0.0,
            Percent50 = hasLatencies ? MicroSecToMs(histogram.GetValueAtPercentile(50.0)) : 0.0,
            Percent75 = hasLatencies ? MicroSecToMs(histogram.GetValueAtPercentile(75.0)) : 0.0,
            Percent95 = hasLatencies ? MicroSecToMs(histogram.GetValueAtPercentile(95.0)) : 0.0,
            Percent99 = hasLatencies ? MicroSecToMs(histogram.GetValueAtPercentile(99.0)) : 0.0,
            StdDev = hasLatencies ? MicroSecToMs(histogram.GetStdDeviation()) : 0.0,
            LatencyCount = new LatencyCount
            {
                LessOrEq800 = stats.LessOrEq800,
                More800Less1200 = stats.More800Less1200,
                MoreOrEq1200 = stats.MoreOrEq1200
            }
        };
    }

    public static DataTransferStats CreateDataTransferStats(RawItemStats stats)
    {
        var histogram = stats.DataTransferHistogram;
        var hasData = histogram.TotalCount > 0L;

        return new DataTransferStats
        {
            MinBytes = hasData ? stats.MinBytes : 0,
            MeanBytes = hasData ? (long)histogram.GetMean() : 0,
            MaxBytes = hasData ? stats.MaxBytes : 0,
            Percent50 = hasData ? histogram.GetValueAtPercentile(50.0) : 0,
            Percent75 = hasData ? histogram.GetValueAtPercentile(75.0) : 0,
            Percent95 = hasData ? histogram.GetValueAtPercentile(95.0) : 0,
            Percent99 = hasData ? histogram.GetValueAtPercentile(99.0) : 0,
            StdDev = hasData ? Converter.Round(histogram.GetStdDeviation(), Constants.StatsRounding) : 0.0,
            AllBytes = stats.AllBytes
        };
    }

    public static StatusCodeStats[] CreateStatusCodeStats(Dictionary<string, RawStatusCodeStats> stats) =>
        stats.Values
            .Select(x => new StatusCodeStats
            {
                StatusCode = x.StatusCode,
                IsError = x.IsError,
                Message = x.Message,
                Count = x.Count
            })
            .ToArray();

    /// <summary>Sums the per-step status codes into one set for the scenario, ordered by code.</summary>
    public static StatusCodeStats[] MergeStatusCodes(IEnumerable<StatusCodeStats> stats) =>
        stats
            .GroupBy(x => x.StatusCode)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new StatusCodeStats
            {
                StatusCode = g.Key,
                IsError = g.First().IsError,
                Message = g.First().Message,
                Count = g.Sum(x => x.Count)
            })
            .ToArray();

    public static MeasurementStats CreateMeasurementStats(RawItemStats raw, TimeSpan duration) => new()
    {
        Request = CreateRequestStats(raw, duration),
        Latency = CreateLatencyStats(raw),
        DataTransfer = CreateDataTransferStats(raw),
        StatusCodes = CreateStatusCodeStats(raw.StatusCodes)
    };

    public static StepStats CreateStepStats(TimeSpan duration, RawMeasurementStats stepData) => new()
    {
        StepName = stepData.Name,
        Ok = CreateMeasurementStats(stepData.OkStats, duration),
        Fail = CreateMeasurementStats(stepData.FailStats, duration)
    };

    public static int GetAllRequestCount(StepStats stats) =>
        stats.Ok.Request.Count + stats.Fail.Request.Count;

    /// <summary>The scenario's own ok/fail numbers, presented as a pseudo-step row.</summary>
    public static StepStats ExtractGlobalInfoStep(ScenarioStats scn) => new()
    {
        StepName = Constants.ScenarioGlobalInfo,
        Ok = scn.Ok,
        Fail = scn.Fail
    };

    public static ScenarioStats EmptyScenarioStats(RuntimeScenario scenario)
    {
        var simulation = scenario.LoadSimulations[0].Value;
        var simulationStats = SimulationPlan.CreateSimulationStats(simulation, 0, 0);

        return new ScenarioStats
        {
            ScenarioName = scenario.ScenarioName,
            Ok = MeasurementStats.Empty,
            Fail = MeasurementStats.Empty,
            StepStats = [],
            LoadSimulationStats = simulationStats,
            CurrentOperation = OperationType.None,
            AllRequestCount = 0,
            AllOkCount = 0,
            AllFailCount = 0,
            AllBytes = 0,
            Duration = TimeSpan.Zero
        };
    }

    public static ScenarioStats CreateScenarioStats(
        string scenarioName,
        RawMeasurementStats[] rawStats,
        LoadSimulationStats simulationStats,
        OperationType currentOperation,
        TimeSpan executedDuration,
        TimeSpan reportingInterval,
        TimeSpan pause)
    {
        // RPS is computed over the window that actually executed, so a pause inside it
        // does not depress the rate.
        var execOnlyDuration = reportingInterval - pause;

        var allStepStats = rawStats.Select(x => CreateStepStats(execOnlyDuration, x)).ToArray();
        var stepStats = allStepStats.Where(x => x.StepName != Constants.ScenarioGlobalInfo).ToArray();
        var globalInfo = allStepStats.FirstOrDefault(x => x.StepName == Constants.ScenarioGlobalInfo);

        var allOkCount = allStepStats.Sum(x => x.Ok.Request.Count);
        var allFailCount = allStepStats.Sum(x => x.Fail.Request.Count);

        MeasurementStats ok, fail;

        if (globalInfo is not null)
        {
            // The scenario's status codes are the union of its steps' status codes.
            ok = globalInfo.Ok with { StatusCodes = MergeStatusCodes(allStepStats.SelectMany(x => x.Ok.StatusCodes)) };
            fail = globalInfo.Fail with { StatusCodes = MergeStatusCodes(allStepStats.SelectMany(x => x.Fail.StatusCodes)) };
        }
        else
        {
            ok = MeasurementStats.Empty;
            fail = MeasurementStats.Empty;
        }

        var allBytes = globalInfo is not null
            ? ok.DataTransfer.AllBytes + fail.DataTransfer.AllBytes
            : allStepStats.Sum(x => x.Ok.DataTransfer.AllBytes + x.Fail.DataTransfer.AllBytes);

        return new ScenarioStats
        {
            ScenarioName = scenarioName,
            Ok = ok,
            Fail = fail,
            StepStats = stepStats,
            LoadSimulationStats = simulationStats,
            CurrentOperation = currentOperation,
            AllRequestCount = allOkCount + allFailCount,
            AllOkCount = allOkCount,
            AllFailCount = allFailCount,
            AllBytes = allBytes,
            Duration = Converter.RoundDuration(executedDuration)
        };
    }

    public static bool FailStatsExist(ScenarioStats stats) =>
        stats.StepStats.Any(x => x.Fail.Request.Count > 0);

    public static long CalcAllBytes(ScenarioStats stats) =>
        stats.Ok.DataTransfer.AllBytes + stats.Fail.DataTransfer.AllBytes;

    public static SessionStats CreateSessionStats(TestInfo testInfo, HostInfo hostInfo, ScenarioStats[] scnStats)
    {
        if (scnStats.Length == 0)
            return SessionStats.Empty with { HostInfo = hostInfo, TestInfo = testInfo };

        var okCount = scnStats.Sum(x => x.AllOkCount);
        var failCount = scnStats.Sum(x => x.AllFailCount);

        return new SessionStats
        {
            ScenarioStats = scnStats,
            PluginStats = [],
            HostInfo = hostInfo,
            TestInfo = testInfo,
            ReportFiles = [],
            AllRequestCount = okCount + failCount,
            AllOkCount = okCount,
            AllFailCount = failCount,
            AllBytes = scnStats.Sum(x => x.AllBytes),
            Duration = Converter.RoundDuration(scnStats.Max(x => x.Duration))
        };
    }
}
