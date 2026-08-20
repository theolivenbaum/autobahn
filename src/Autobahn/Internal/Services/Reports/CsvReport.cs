using System.Globalization;
using Autobahn.Metrics;
using Autobahn.Stats;
using Autobahn.Thresholds;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Autobahn.Internal.Services.Reports;

/// <summary>One row per step, for feeding a spreadsheet or a diff.</summary>
internal static class CsvReport
{
    private const string Separator = ",";

    public static string Print(ILogger logger, SessionResult sessionResult)
    {
        try
        {
            logger.ZLogTrace($"CsvReport.print");

            var body = sessionResult.FinalStats.ScenarioStats
                .Select(scnStats => PrintSteps(sessionResult.FinalStats.TestInfo, scnStats))
                .ConcatLines();

            return GetHeader() + Environment.NewLine + body;
        }
        catch (Exception ex)
        {
            logger.ZLogError($"CsvReport.print failed: {ex}");
            return "Could not generate report";
        }
    }

    /// <summary>
    /// The metrics as their own CSV, because they do not fit one-row-per-step: a metric is a
    /// series over the run, not a property of a step. Empty when the run collected none.
    /// </summary>
    public static string PrintMetrics(ILogger logger, SessionResult sessionResult)
    {
        try
        {
            var metrics = sessionResult.FinalStats.Metrics;
            if (metrics.Length == 0)
                return string.Empty;

            var testInfo = sessionResult.FinalStats.TestInfo;

            var header = string.Join(Separator,
                "test_suite", "test_name", "metric", "kind", "unit",
                "current", "min", "mean", "max",
                "50_percent", "75_percent", "95_percent", "99_percent", "writes");

            var body = metrics.Select(m => Line(testInfo, m)).ConcatLines();

            return header + Environment.NewLine + body;
        }
        catch (Exception ex)
        {
            logger.ZLogError($"CsvReport.printMetrics failed: {ex}");
            return "Could not generate report";
        }

        static string Line(TestInfo testInfo, MetricStats m)
        {
            object[] cells =
            [
                testInfo.TestSuite, testInfo.TestName,
                m.Name, m.Kind.ToString().ToLowerInvariant(), m.Unit,
                m.Current, m.Min, m.Mean, m.Max,
                m.Percent50, m.Percent75, m.Percent95, m.Percent99, m.Count
            ];

            return string.Join(Separator, cells.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>
    /// The thresholds as their own CSV, for the same reason the metrics get one: a rule is a
    /// verdict about the run, not a property of a step. Empty when the run declared none.
    /// </summary>
    public static string PrintThresholds(ILogger logger, SessionResult sessionResult)
    {
        try
        {
            var thresholds = sessionResult.FinalStats.Thresholds;
            if (thresholds.Length == 0)
                return string.Empty;

            var testInfo = sessionResult.FinalStats.TestInfo;

            var header = string.Join(Separator,
                "test_suite", "test_name", "threshold", "scope", "scenario", "subject",
                "comparison", "target", "observed", "failed_checks", "total_checks",
                "first_failed_at", "passed", "aborted");

            var body = thresholds.Select(t => Line(testInfo, t)).ConcatLines();

            return header + Environment.NewLine + body;
        }
        catch (Exception ex)
        {
            logger.ZLogError($"CsvReport.printThresholds failed: {ex}");
            return "Could not generate report";
        }

        static string Line(TestInfo testInfo, ThresholdResult t)
        {
            object[] cells =
            [
                testInfo.TestSuite, testInfo.TestName,
                Quote(t.Name), t.Scope, t.ScenarioName, t.Subject,
                ReportHelper.Symbol(t.Comparison), t.Value, t.ObservedValue,
                t.FailedChecks, t.TotalChecks,
                t.FirstFailedAt?.ToString() ?? "", t.Passed, t.Aborted
            ];

            return string.Join(Separator, cells.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>
    /// A threshold's generated description carries the comparison and the target, so it can
    /// hold a comma; nothing else in these files can.
    /// </summary>
    private static string Quote(string value) =>
        value.Contains(Separator) ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static string GetHeader() =>
        string.Join(Separator,
            "test_suite", "test_name",
            "scenario", "duration", "step_name",
            "request_count", "ok", "failed",
            "rps", "min", "mean", "max",
            "50_percent", "75_percent", "95_percent", "99_percent", "std_dev",
            "data_transfer_min_kb", "data_transfer_mean_kb", "data_transfer_max_kb", "data_transfer_all_mb");

    private static string PrintSteps(TestInfo testInfo, ScenarioStats scnStats) =>
        scnStats.StepStats
            .Select(stepStats => GetLine(scnStats.ScenarioName, scnStats.Duration, stepStats, testInfo))
            .ConcatLines();

    private static string GetLine(string scenarioName, TimeSpan duration, StepStats stats, TestInfo testInfo)
    {
        var okCount = stats.Ok.Request.Count;
        var failCount = stats.Fail.Request.Count;
        var lt = stats.Ok.Latency;
        var dt = stats.Ok.DataTransfer;

        object[] cells =
        [
            testInfo.TestSuite, testInfo.TestName,
            scenarioName, duration, stats.StepName,
            okCount + failCount, okCount, failCount,
            stats.Ok.Request.RPS, lt.MinMs, lt.MeanMs, lt.MaxMs,
            lt.Percent50, lt.Percent75, lt.Percent95, lt.Percent99, lt.StdDev,
            Converter.FromBytesToKb(dt.MinBytes), Converter.FromBytesToKb(dt.MeanBytes),
            Converter.FromBytesToKb(dt.MaxBytes), Converter.FromBytesToMb(dt.AllBytes)
        ];

        return string.Join(Separator,
            cells.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)));
    }
}
