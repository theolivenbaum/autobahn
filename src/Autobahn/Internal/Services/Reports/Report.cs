using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Internal.Infra;
using Autobahn.Stats;
using Microsoft.Extensions.Logging;
using Spectre.Console.Rendering;
using ZLogger;

namespace Autobahn.Internal.Services.Reports;

/// <summary>Every report format, each built only if someone asks for it.</summary>
internal sealed record ReportsContent
{
    public required Lazy<string> TxtReport { get; init; }
    public required Lazy<string> HtmlReport { get; init; }
    public required Lazy<string> CsvReport { get; init; }

    /// <summary>The metrics as their own CSV. Empty when the run collected none.</summary>
    public required Lazy<string> CsvMetricsReport { get; init; }

    /// <summary>The thresholds as their own CSV. Empty when the run declared none.</summary>
    public required Lazy<string> CsvThresholdsReport { get; init; }
    public required Lazy<string> MdReport { get; init; }

    /// <summary>The versioned run artifact. Every other format is a rendering of the same data.</summary>
    public required Lazy<string> JsonReport { get; init; }
    public required Lazy<List<IRenderable>> ConsoleReport { get; init; }
}

/// <summary>Builds the end-of-run reports and writes the requested ones to disk.</summary>
internal static class Report
{
    public static ReportsContent Build(
        ILogger logger, SessionResult sessionResult, IReadOnlyList<RuntimeScenario> targetScenarios)
    {
        logger.ZLogTrace($"Report.build");

        var simulations = GetLoadSimulations(targetScenarios);
        var newSessionResult = AppendGlobalInfoStep(sessionResult);

        return new ReportsContent
        {
            TxtReport = new Lazy<string>(() => Reports.TxtReport.Print(logger, newSessionResult, simulations)),
            HtmlReport = new Lazy<string>(() => Reports.HtmlReport.Print(logger, newSessionResult)),
            CsvReport = new Lazy<string>(() => Reports.CsvReport.Print(logger, newSessionResult)),
            CsvMetricsReport = new Lazy<string>(() => Reports.CsvReport.PrintMetrics(logger, newSessionResult)),
            CsvThresholdsReport = new Lazy<string>(() => Reports.CsvReport.PrintThresholds(logger, newSessionResult)),
            MdReport = new Lazy<string>(() => Reports.MdReport.Print(logger, newSessionResult, simulations)),
            // The artifact serializes the run as measured, so it gets the session result
            // before the reports fold the scenario's own numbers in as a pseudo-step.
            JsonReport = new Lazy<string>(() => Reports.JsonReport.Print(logger, sessionResult, targetScenarios)),
            ConsoleReport = new Lazy<List<IRenderable>>(() => Reports.ConsoleReport.Print(logger, newSessionResult, simulations))
        };
    }

    private static Dictionary<string, IReadOnlyList<LoadSimulation>> GetLoadSimulations(
        IReadOnlyList<RuntimeScenario> scenarios) =>
        scenarios.ToDictionary(
            scn => scn.ScenarioName,
            scn => (IReadOnlyList<LoadSimulation>)scn.LoadSimulations.Select(x => x.Value).ToList());

    /// <summary>
    /// The reports show the scenario's own numbers as a first row alongside its steps, so the
    /// scenario row is folded into the step list before rendering.
    /// </summary>
    private static SessionResult AppendGlobalInfoStep(SessionResult sessionResult)
    {
        var timeLineHistory = sessionResult.TimeLineHistory
            .Select(historyItem => historyItem with
            {
                ScenarioStats = historyItem.ScenarioStats.Select(WithGlobalInfoStep).ToArray()
            })
            .ToArray();

        var finalStats = sessionResult.FinalStats with
        {
            ScenarioStats = sessionResult.FinalStats.ScenarioStats.Select(WithGlobalInfoStep).ToArray()
        };

        return sessionResult with { TimeLineHistory = timeLineHistory, FinalStats = finalStats };

        static ScenarioStats WithGlobalInfoStep(ScenarioStats scn) =>
            scn with { StepStats = [Statistics.ExtractGlobalInfoStep(scn), .. scn.StepStats] };
    }

    public static SessionStats Save(
        IGlobalDependency dep, SessionArgs sessionArgs, SessionStats stats, ReportsContent report)
    {
        foreach (var renderable in report.ConsoleReport.Value)
            ConsoleRender.Render(renderable);

        if (sessionArgs.ReportFormats.Count == 0)
            return stats;

        var reportFiles = SaveToFolder(
            dep, sessionArgs.ReportFolder, sessionArgs.ReportFileName, sessionArgs.ReportFormats, report);

        return stats with { ReportFiles = reportFiles };
    }

    private static ReportFile[] SaveToFolder(
        IGlobalDependency dep,
        string folder,
        string fileName,
        IReadOnlyList<ReportFormat> reportFormats,
        ReportsContent report)
    {
        try
        {
            Directory.CreateDirectory(folder);

            var reportFiles = reportFormats.SelectMany(format => BuildReportFiles(folder, fileName, format, report)).ToArray();

            foreach (var file in reportFiles)
            {
                try
                {
                    File.WriteAllText(file.FilePath, file.ReportContent);
                }
                catch (Exception ex)
                {
                    dep.LogError(ex, $"Could not save the report file {file.FilePath}");
                }
            }

            if (reportFiles.Length > 0)
                dep.LogInfo($"Reports saved in folder: {new DirectoryInfo(folder).FullName}");

            return reportFiles;
        }
        catch (Exception ex)
        {
            dep.LogError(ex, "Report.save failed");
            return [];
        }
    }

    /// <summary>
    /// The files one requested format produces. All but CSV produce exactly one; CSV also
    /// writes the metrics beside it, because a metric is a series over the run rather than a
    /// property of a step and does not fit the one-row-per-step shape.
    /// </summary>
    private static IEnumerable<ReportFile> BuildReportFiles(
        string folder, string fileName, ReportFormat format, ReportsContent report)
    {
        var (fileExt, content) = format switch
        {
            ReportFormat.Txt => (".txt", report.TxtReport),
            ReportFormat.Html => (".html", report.HtmlReport),
            ReportFormat.Csv => (".csv", report.CsvReport),
            ReportFormat.Md => (".md", report.MdReport),
            ReportFormat.Json => (".json", report.JsonReport),
            _ => throw new NotSupportedException($"Invalid report format: {format}")
        };

        yield return new ReportFile
        {
            FilePath = Path.Combine(folder, fileName) + fileExt,
            ReportFormat = format,
            ReportContent = content.Value
        };

        if (format != ReportFormat.Csv)
            yield break;

        if (report.CsvMetricsReport.Value.Length > 0)
        {
            yield return new ReportFile
            {
                FilePath = Path.Combine(folder, fileName) + "_metrics.csv",
                ReportFormat = ReportFormat.Csv,
                ReportContent = report.CsvMetricsReport.Value
            };
        }

        if (report.CsvThresholdsReport.Value.Length > 0)
        {
            yield return new ReportFile
            {
                FilePath = Path.Combine(folder, fileName) + "_thresholds.csv",
                ReportFormat = ReportFormat.Csv,
                ReportContent = report.CsvThresholdsReport.Value
            };
        }
    }
}
