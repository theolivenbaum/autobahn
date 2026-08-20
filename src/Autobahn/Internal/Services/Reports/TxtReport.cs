using Autobahn.Internal.Domain.Stats;
using Autobahn.Stats;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Autobahn.Internal.Services.Reports;

/// <summary>The plain-text report.</summary>
internal static class TxtReport
{
    private static string Plain(object? value) => value?.ToString() ?? string.Empty;

    public static string Print(
        ILogger logger,
        SessionResult sessionResult,
        IReadOnlyDictionary<string, IReadOnlyList<LoadSimulation>> simulations)
    {
        try
        {
            logger.ZLogTrace($"TxtReport.print");

            return new[]
            {
                PrintTestInfo(sessionResult.FinalStats.TestInfo),
                PrintSessionStats(sessionResult.FinalStats, simulations),
                PrintMetrics(sessionResult.FinalStats),
                PrintThresholds(sessionResult.FinalStats),
                PrintPluginStats(sessionResult.FinalStats),
                PrintHints(sessionResult.Hints)
            }.ConcatLines();
        }
        catch (Exception ex)
        {
            logger.ZLogError($"TxtReport.print failed: {ex}");
            return "Could not generate report";
        }
    }

    private static string PrintTestInfo(TestInfo testInfo) =>
        new[]
        {
            "test info",
            $"test suite: {testInfo.TestSuite}",
            $"test name: {testInfo.TestName}",
            $"session id: {testInfo.SessionId}"
        }.ConcatLines().AppendNewLine();

    private static string PrintSessionStats(
        SessionStats stats, IReadOnlyDictionary<string, IReadOnlyList<LoadSimulation>> loadSimulations) =>
        stats.ScenarioStats
            .SelectMany(scnStats => PrintScenarioStats(scnStats, loadSimulations[scnStats.ScenarioName]))
            .ConcatLines();

    private static IEnumerable<string> PrintScenarioStats(ScenarioStats scnStats, IReadOnlyList<LoadSimulation> simulations)
    {
        yield return PrintScenarioHeader(scnStats).AppendNewLine();
        yield return PrintLoadSimulations(simulations).AppendNewLine();
        yield return PrintStepStatsTable(isOkStats: true, scnStats);

        if (Statistics.FailStatsExist(scnStats))
            yield return PrintStepStatsTable(isOkStats: false, scnStats);

        yield return $"status codes for scenario: {scnStats.ScenarioName}";
        yield return PrintStatusCodeTable(scnStats);
    }

    private static string PrintScenarioHeader(ScenarioStats scnStats) =>
        $"scenario: {scnStats.ScenarioName}{Environment.NewLine}"
        + $"  - ok count: {scnStats.Ok.Request.Count}{Environment.NewLine}"
        + $"  - fail count: {scnStats.Fail.Request.Count}{Environment.NewLine}"
        + $"  - all data: {ReportHelper.PrintAllData(Plain, Statistics.CalcAllBytes(scnStats))}{Environment.NewLine}"
        + $"  - duration: {scnStats.Duration}";

    private static string PrintLoadSimulations(IReadOnlyList<LoadSimulation> simulations)
    {
        var list = simulations.Select(x => ReportHelper.PrintLoadSimulation(Plain, x)).ConcatLines();
        return $"load simulations: {Environment.NewLine}{list}";
    }

    private static string PrintStepStatsTable(bool isOkStats, ScenarioStats scnStats)
    {
        var table = new TextTable("step", isOkStats ? "ok stats" : "fail stats");

        var rows = scnStats.StepStats
            .SelectMany((stats, i) => ReportHelper.PrintStepStatsRow(isOkStats, Plain, Plain, Plain, i, stats));

        foreach (var row in rows)
            table.AddRow(row[0], row[1]);

        return table.ToString();
    }

    private static string PrintStatusCodeTable(ScenarioStats scnStats)
    {
        var table = new TextTable("status code", "count", "message");

        foreach (var row in ReportHelper.CreateStatusCodeTableRows(Plain, Plain, scnStats))
            table.AddRow(row[0], row[1], row[2]);

        return table.ToString();
    }

    private static string PrintMetrics(SessionStats stats)
    {
        if (stats.Metrics.Length == 0)
            return string.Empty;

        var table = new TextTable(ReportHelper.MetricTableHeaders);

        foreach (var row in ReportHelper.CreateMetricTableRows(Plain, Plain, stats.Metrics))
            table.AddRow([.. row]);

        return new[] { "metrics", table.ToString() }.ConcatLines();
    }

    private static string PrintThresholds(SessionStats stats)
    {
        if (stats.Thresholds.Length == 0)
            return string.Empty;

        var table = new TextTable(ReportHelper.ThresholdTableHeaders);

        foreach (var row in ReportHelper.CreateThresholdTableRows(Plain, Plain, Plain, stats.Thresholds))
            table.AddRow([.. row]);

        var verdict = stats.AllThresholdsPassed
            ? "thresholds: all passed"
            : $"thresholds: {stats.Thresholds.Count(x => !x.Passed)} of {stats.Thresholds.Length} FAILED";

        return new[] { verdict, table.ToString() }.ConcatLines();
    }

    private static string PrintPluginStats(SessionStats stats) =>
        stats.PluginStats
            .SelectMany(dataSet => dataSet.GetTables())
            .SelectMany(table => new[]
            {
                $"plugin stats: '{table.TableName}'",
                PrintPluginStatsTable(table)
            })
            .ConcatLines();

    private static string PrintPluginStatsTable(System.Data.DataTable table)
    {
        var columnNames = table.GetColumns().Select(x => x.ColumnName).ToArray();
        var columnCaptions = table.GetColumns().Select(x => x.GetColumnCaptionOrName()).ToArray();
        var textTable = new TextTable(columnCaptions);

        foreach (var row in table.GetRows())
            textTable.AddRow(columnNames.Select(name => row[name]).ToArray());

        return textTable.ToString();
    }

    private static string PrintHints(HintResult[] hints)
    {
        if (hints.Length == 0)
            return string.Empty;

        var table = new TextTable("source", "name", "hint");

        foreach (var hint in hints)
            table.AddRow(hint.SourceType, hint.SourceName, hint.Hint);

        return new[] { "hints:", table.ToString() }.ConcatLines();
    }
}
