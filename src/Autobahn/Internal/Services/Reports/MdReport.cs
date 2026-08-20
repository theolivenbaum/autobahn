using System.Data;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Stats;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Autobahn.Internal.Services.Reports;

/// <summary>The markdown report - the one that pastes into a pull request.</summary>
internal static class MdReport
{
    private static string Code(object? value) => MarkdownDocument.InlineCode(value);

    public static string Print(
        ILogger logger,
        SessionResult sessionResult,
        IReadOnlyDictionary<string, IReadOnlyList<LoadSimulation>> simulations)
    {
        try
        {
            logger.ZLogTrace($"MdReport.print");

            var doc = new MarkdownDocument();

            PrintTestInfo(doc, sessionResult.FinalStats.TestInfo);
            PrintSessionStats(doc, sessionResult.FinalStats, simulations);
            PrintMetrics(doc, sessionResult.FinalStats);
            PrintThresholds(doc, sessionResult.FinalStats);
            PrintPluginStats(doc, sessionResult.FinalStats);
            PrintHints(doc, sessionResult.Hints);

            return doc.ToString();
        }
        catch (Exception ex)
        {
            logger.ZLogError($"MdReport.print failed: {ex}");
            return "Could not generate report";
        }
    }

    private static void PrintTestInfo(MarkdownDocument doc, TestInfo testInfo) =>
        doc.AddHeader("test info")
            .AddText($"test suite: {Code(testInfo.TestSuite)}").AddBlankLine()
            .AddText($"test name: {Code(testInfo.TestName)}").AddBlankLine()
            .AddText($"session id: {Code(testInfo.SessionId)}").AddBlankLine();

    private static void PrintSessionStats(
        MarkdownDocument doc,
        SessionStats stats,
        IReadOnlyDictionary<string, IReadOnlyList<LoadSimulation>> loadSimulations)
    {
        foreach (var scnStats in stats.ScenarioStats)
        {
            PrintScenarioStats(doc, scnStats, loadSimulations[scnStats.ScenarioName]);
            doc.AddBlankLine();
        }
    }

    private static void PrintScenarioStats(
        MarkdownDocument doc, ScenarioStats scnStats, IReadOnlyList<LoadSimulation> simulations)
    {
        doc.AddHeader("scenario stats");

        doc.AddText($"scenario: {Code(scnStats.ScenarioName)}").AddBlankLine()
            .AddText($"  - ok count: {Code(scnStats.Ok.Request.Count)}").AddBlankLine()
            .AddText($"  - fail count: {Code(scnStats.Fail.Request.Count)}").AddBlankLine()
            .AddText($"  - all data: {ReportHelper.PrintAllData(Code, Statistics.CalcAllBytes(scnStats))}").AddBlankLine()
            .AddText($"  - duration: {Code(scnStats.Duration)}").AddBlankLine();

        doc.AddText("load simulations:").AddBlankLine();

        foreach (var simulation in simulations)
            doc.AddText(ReportHelper.PrintLoadSimulation(Code, simulation)).AddBlankLine();

        PrintStepStatsTable(doc, isOkStats: true, scnStats);

        if (Statistics.FailStatsExist(scnStats))
            PrintStepStatsTable(doc, isOkStats: false, scnStats);

        doc.AddHeader($"status codes for scenario: {Code(scnStats.ScenarioName)}");
        doc.AddTable(["status code", "count", "message"], ReportHelper.CreateStatusCodeTableRows(Code, Code, scnStats));
    }

    private static void PrintStepStatsTable(MarkdownDocument doc, bool isOkStats, ScenarioStats scnStats)
    {
        var headers = new[] { "step", isOkStats ? "ok stats" : "fail stats" };

        var rows = scnStats.StepStats
            .SelectMany((stats, i) => ReportHelper.PrintStepStatsRow(isOkStats, Code, Code, Code, i, stats))
            .Cast<IReadOnlyList<string>>()
            .ToList();

        doc.AddTable(headers, rows).AddBlankLine();
    }

    private static void PrintMetrics(MarkdownDocument doc, SessionStats stats)
    {
        if (stats.Metrics.Length == 0)
            return;

        var rows = ReportHelper.CreateMetricTableRows(Code, Code, stats.Metrics)
            .Cast<IReadOnlyList<string>>()
            .ToList();

        doc.AddHeader("metrics");
        doc.AddTable(ReportHelper.MetricTableHeaders, rows).AddBlankLine();
    }

    private static void PrintThresholds(MarkdownDocument doc, SessionStats stats)
    {
        if (stats.Thresholds.Length == 0)
            return;

        var rows = ReportHelper.CreateThresholdTableRows(Code, Code, Code, stats.Thresholds)
            .Cast<IReadOnlyList<string>>()
            .ToList();

        doc.AddHeader(stats.AllThresholdsPassed
            ? "thresholds: all passed"
            : $"thresholds: {stats.Thresholds.Count(x => !x.Passed)} of {stats.Thresholds.Length} FAILED");

        doc.AddTable(ReportHelper.ThresholdTableHeaders, rows).AddBlankLine();
    }

    private static void PrintPluginStats(MarkdownDocument doc, SessionStats stats)
    {
        foreach (var table in stats.PluginStats.SelectMany(dataSet => dataSet.GetTables()))
        {
            doc.AddHeader($"plugin stats: {Code(table.TableName)}");

            var headers = table.GetColumns().Select(col => col.GetColumnCaptionOrName()).ToList();
            var columns = table.GetColumns();

            var rows = table.GetRows()
                .Select(row => (IReadOnlyList<string>)columns.Select(col => row[col]?.ToString() ?? "").ToList())
                .ToList();

            doc.AddTable(headers, rows).AddBlankLine();
        }
    }

    private static void PrintHints(MarkdownDocument doc, HintResult[] hints)
    {
        if (hints.Length == 0)
            return;

        var rows = hints
            .Select(hint => (IReadOnlyList<string>)new List<string> { hint.SourceType.ToString(), hint.SourceName, hint.Hint })
            .ToList();

        doc.AddHeader("hints:");
        doc.AddTable(["source", "name", "hint"], rows);
    }
}
