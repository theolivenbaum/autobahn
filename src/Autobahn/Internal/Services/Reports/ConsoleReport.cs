using System.Data;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Internal.Infra;
using Autobahn.Stats;
using Microsoft.Extensions.Logging;
using Spectre.Console.Rendering;
using ZLogger;

namespace Autobahn.Internal.Services.Reports;

/// <summary>The end-of-run summary printed to the terminal.</summary>
internal static class ConsoleReport
{
    public static List<IRenderable> Print(
        ILogger logger,
        SessionResult sessionResult,
        IReadOnlyDictionary<string, IReadOnlyList<LoadSimulation>> simulations)
    {
        try
        {
            logger.ZLogTrace($"ConsoleReport.print");

            var result = new List<IRenderable> { ConsoleRender.AddLine(string.Empty) };

            result.AddRange(PrintTestInfo(sessionResult.FinalStats.TestInfo));
            result.AddRange(PrintSessionStats(sessionResult.FinalStats, simulations));
            result.AddRange(PrintMetrics(sessionResult.FinalStats));
            result.AddRange(PrintThresholds(sessionResult.FinalStats));
            result.AddRange(PrintPluginStats(sessionResult.FinalStats));
            result.AddRange(PrintHints(sessionResult.Hints));
            result.Add(ConsoleRender.AddLine(string.Empty));

            return result;
        }
        catch (Exception ex)
        {
            logger.ZLogError($"ConsoleReport.print failed: {ex}");
            return [ConsoleRender.AddLine("Could not generate report")];
        }
    }

    private static List<IRenderable> PrintTestInfo(TestInfo testInfo) =>
    [
        ConsoleRender.AddHeader("test info"),
        ConsoleRender.AddLine(string.Empty),
        ConsoleRender.AddLine($"test suite: {ConsoleRender.OkEscColor(testInfo.TestSuite)}"),
        ConsoleRender.AddLine($"test name: {ConsoleRender.OkEscColor(testInfo.TestName)}"),
        ConsoleRender.AddLine($"session id: {ConsoleRender.OkEscColor(testInfo.SessionId)}"),
        ConsoleRender.AddLine(string.Empty)
    ];

    private static List<IRenderable> PrintSessionStats(
        SessionStats stats, IReadOnlyDictionary<string, IReadOnlyList<LoadSimulation>> loadSimulations)
    {
        var result = new List<IRenderable>
        {
            ConsoleRender.AddHeader("scenario stats"),
            ConsoleRender.AddLine(string.Empty)
        };

        foreach (var scnStats in stats.ScenarioStats)
        {
            result.AddRange(PrintScenarioStats(scnStats, loadSimulations[scnStats.ScenarioName]));
            result.Add(ConsoleRender.AddLine(string.Empty));
        }

        return result;
    }

    private static List<IRenderable> PrintScenarioStats(ScenarioStats scnStats, IReadOnlyList<LoadSimulation> simulations)
    {
        var result = new List<IRenderable>
        {
            ConsoleRender.AddLine($"scenario: {ConsoleRender.OkEscColor(scnStats.ScenarioName)}"),
            ConsoleRender.AddLine($"  - ok count: {ConsoleRender.OkEscColor(scnStats.Ok.Request.Count)}"),
            ConsoleRender.AddLine($"  - fail count: {ConsoleRender.ErrorEscColor(scnStats.Fail.Request.Count)}"),
            ConsoleRender.AddLine($"  - all data: {ReportHelper.PrintAllData(ConsoleRender.OkEscColor, Statistics.CalcAllBytes(scnStats))}"),
            ConsoleRender.AddLine($"  - duration: {ConsoleRender.OkEscColor(scnStats.Duration)}"),
            ConsoleRender.AddLine(string.Empty),
            ConsoleRender.AddLine("load simulations: ")
        };

        result.AddRange(simulations.Select(x =>
            ConsoleRender.AddLine(ReportHelper.PrintLoadSimulation(ConsoleRender.OkEscColor, x))));

        result.Add(ConsoleRender.AddLine(string.Empty));
        result.Add(PrintStepStatsTable(isOkStats: true, scnStats));

        if (Statistics.FailStatsExist(scnStats))
            result.Add(PrintStepStatsTable(isOkStats: false, scnStats));

        result.Add(ConsoleRender.AddLine(string.Empty));
        result.Add(ConsoleRender.AddLine($"status codes for scenario: {ConsoleRender.OkColor(scnStats.ScenarioName)}"));

        result.Add(ConsoleRender.AddTable(
            ["status code", "count", "message"],
            ReportHelper.CreateStatusCodeTableRows(ConsoleRender.OkEscColor, ConsoleRender.ErrorEscColor, scnStats)));

        return result;
    }

    private static IRenderable PrintStepStatsTable(bool isOkStats, ScenarioStats scnStats)
    {
        var headers = new[] { "step", isOkStats ? "ok stats" : "fail stats" };

        var rows = scnStats.StepStats
            .SelectMany((stats, i) => ReportHelper.PrintStepStatsRow(
                isOkStats, ConsoleRender.OkEscColor, ConsoleRender.ErrorEscColor, ConsoleRender.BlueEscColor, i, stats))
            .Cast<IReadOnlyList<string>>();

        return ConsoleRender.AddTable(headers, rows);
    }

    private static List<IRenderable> PrintMetrics(SessionStats stats)
    {
        if (stats.Metrics.Length == 0)
            return [];

        var rows = ReportHelper
            .CreateMetricTableRows(ConsoleRender.OkEscColor, ConsoleRender.BlueEscColor, stats.Metrics)
            .Cast<IReadOnlyList<string>>();

        return
        [
            ConsoleRender.AddHeader("metrics"),
            ConsoleRender.AddLine(string.Empty),
            ConsoleRender.AddTable(ReportHelper.MetricTableHeaders, rows),
            ConsoleRender.AddLine(string.Empty)
        ];
    }

    private static List<IRenderable> PrintThresholds(SessionStats stats)
    {
        if (stats.Thresholds.Length == 0)
            return [];

        var failed = stats.Thresholds.Count(x => !x.Passed);

        var verdict = failed == 0
            ? ConsoleRender.OkEscColor("all passed")
            : ConsoleRender.ErrorEscColor($"{failed} of {stats.Thresholds.Length} FAILED");

        var rows = ReportHelper
            .CreateThresholdTableRows(
                ConsoleRender.OkEscColor, ConsoleRender.ErrorEscColor, ConsoleRender.BlueEscColor, stats.Thresholds)
            .Cast<IReadOnlyList<string>>();

        return
        [
            ConsoleRender.AddHeader("thresholds"),
            ConsoleRender.AddLine(string.Empty),
            ConsoleRender.AddLine($"verdict: {verdict}"),
            ConsoleRender.AddLine(string.Empty),
            ConsoleRender.AddTable(ReportHelper.ThresholdTableHeaders, rows),
            ConsoleRender.AddLine(string.Empty)
        ];
    }

    private static List<IRenderable> PrintPluginStats(SessionStats stats)
    {
        if (stats.PluginStats.Length == 0)
            return [];

        var result = new List<IRenderable>
        {
            ConsoleRender.AddHeader("plugin stats"),
            ConsoleRender.AddLine(string.Empty)
        };

        foreach (var table in stats.PluginStats.SelectMany(dataSet => dataSet.GetTables()))
        {
            result.Add(ConsoleRender.AddLine($"plugin stats: {ConsoleRender.OkEscColor(table.TableName)}"));
            result.Add(ConsoleRender.AddLine(string.Empty));
            result.Add(CreatePluginStatsTable(table));
            result.Add(ConsoleRender.AddLine(string.Empty));
        }

        return result;
    }

    private static IRenderable CreatePluginStatsTable(DataTable table)
    {
        var columns = table.GetColumns();
        var rows = new List<IReadOnlyList<string>>();
        var index = 0;

        foreach (var row in table.GetRows())
        {
            if (index > 0)
                rows.Add([string.Empty, string.Empty]);

            rows.AddRange(columns.Select(col => (IReadOnlyList<string>)new List<string>
            {
                ConsoleRender.EscapeMarkup(col.GetColumnCaptionOrName()),
                ConsoleRender.EscapeMarkup(row[col]?.ToString() ?? "")
            }));

            index++;
        }

        return ConsoleRender.AddTable(["key", "value"], rows);
    }

    private static List<IRenderable> PrintHints(HintResult[] hints)
    {
        if (hints.Length == 0)
            return [];

        var result = new List<IRenderable>
        {
            ConsoleRender.AddHeader("hints"),
            ConsoleRender.AddLine(string.Empty)
        };

        result.AddRange(ConsoleRender.AddList(hints.Select(hint => new[]
        {
            $"hint for {hint.SourceType} {ConsoleRender.OkEscColor(hint.SourceName)}:",
            $"{ConsoleRender.WarningEscColor(hint.Hint)}"
        })));

        return result;
    }
}
