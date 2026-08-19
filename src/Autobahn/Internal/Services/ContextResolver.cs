using Microsoft.Extensions.Logging;
using Autobahn.Configuration;
using Autobahn.Internal.Domain;
using Autobahn.Stats;

namespace Autobahn.Internal.Services;

/// <summary>
/// Works out the effective value of every setting, given the code defaults and the JSON
/// config, and validates the result.
/// </summary>
/// <remarks>The JSON config wins over anything set in code, for every setting below.</remarks>
internal static class ContextResolver
{
    public static Result<IReadOnlyList<string>> CheckAvailableTargets(
        IReadOnlyList<ScenarioProps> regScenarios, IReadOnlyList<string> targetScenarios)
    {
        var allScenarios = regScenarios.Select(x => x.ScenarioName).ToList();
        var notFoundScenarios = targetScenarios.Except(allScenarios).ToList();

        if (allScenarios.Count == 0)
            return Result<IReadOnlyList<string>>.Fail(new ScenarioError.EmptyRegisteredScenarios());

        return notFoundScenarios.Count == 0
            ? Result<IReadOnlyList<string>>.Ok(targetScenarios)
            : Result<IReadOnlyList<string>>.Fail(new ScenarioError.TargetScenariosNotFound(notFoundScenarios, allScenarios));
    }

    public static Result<string> CheckReportName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result<string>.Fail(new ReportError.EmptyReportName());

        return name.IndexOfAny(Path.GetInvalidFileNameChars()) != -1
            ? Result<string>.Fail(new ReportError.InvalidReportName())
            : Result<string>.Ok(name);
    }

    public static Result<string> CheckReportFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return Result<string>.Fail(new ReportError.EmptyReportFolderPath());

        return folderPath.IndexOfAny(Path.GetInvalidPathChars()) != -1
            ? Result<string>.Fail(new ReportError.InvalidReportFolderPath())
            : Result<string>.Ok(folderPath);
    }

    public static Result<TimeSpan> CheckReportingInterval(TimeSpan interval) =>
        interval >= Constants.MinReportingInterval
            ? Result<TimeSpan>.Ok(interval)
            : Result<TimeSpan>.Fail(new ReportError.ReportingIntervalSmallerThanMin());

    public static Result<IReadOnlyList<ScenarioSetting>> CheckDuplicateScenarioSettings(
        IReadOnlyList<ScenarioSetting> settings)
    {
        var duplicates = settings.Select(x => x.ScenarioName).FilterDuplicates().ToList();

        return duplicates.Count > 0
            ? Result<IReadOnlyList<ScenarioSetting>>.Fail(new ScenarioError.DuplicateScenarioNamesInConfig(duplicates))
            : Result<IReadOnlyList<ScenarioSetting>>.Ok(settings);
    }

    private static GlobalSettings? Global(AutobahnContext context) => context.Config?.GlobalSettings;

    public static string GetTestSuite(AutobahnContext context) => context.Config?.TestSuite ?? context.TestSuite;

    public static string GetTestName(AutobahnContext context) => context.Config?.TestName ?? context.TestName;

    public static Result<IReadOnlyList<ScenarioSetting>> GetScenariosSettings(AutobahnContext context)
    {
        var settings = Global(context)?.ScenariosSettings;

        return settings is null
            ? Result<IReadOnlyList<ScenarioSetting>>.Ok([])
            : CheckDuplicateScenarioSettings(settings);
    }

    public static IReadOnlyList<string> GetTargetScenarios(AutobahnContext context) =>
        context.TargetScenarios
        ?? context.Config?.TargetScenarios
        ?? context.RegisteredScenarios.Select(x => x.ScenarioName).ToList();

    public static AutobahnContext SetTargetScenarios(IReadOnlyList<string> scenarios, AutobahnContext context) =>
        context with { TargetScenarios = scenarios };

    public static string? GetReportFileName(AutobahnContext context) =>
        Global(context)?.ReportFileName ?? context.Reporting.FileName;

    public static string GetReportFileNameOrDefault(DateTime currentTime, AutobahnContext context) =>
        GetReportFileName(context)
        ?? $"{Constants.DefaultReportName}_{currentTime:yyyy-MM-dd--HH-mm-ss}";

    public static bool GetEnableHintsAnalyzer(AutobahnContext context) =>
        Global(context)?.EnableHintsAnalyzer ?? context.EnableHintsAnalyzer;

    public static bool GetEnableStopTestForcibly(AutobahnContext context) =>
        Global(context)?.EnableStopTestForcibly ?? context.EnableStopTestForcibly;

    private static string? GetReportFolder(AutobahnContext context) =>
        Global(context)?.ReportFolder ?? context.Reporting.FolderName;

    public static string GetReportFolderOrDefault(string sessionId, AutobahnContext context) =>
        GetReportFolder(context) ?? Path.Combine(Constants.DefaultReportFolder, sessionId);

    public static IReadOnlyList<ReportFormat> GetReportFormats(AutobahnContext context) =>
        Global(context)?.ReportFormats ?? context.Reporting.Formats;

    public static Result<TimeSpan> GetReportingInterval(AutobahnContext context) =>
        CheckReportingInterval(Global(context)?.ReportingInterval ?? context.Reporting.ReportingInterval);

    public static Result<SessionArgs> CreateSessionArgs(TestInfo testInfo, AutobahnContext context)
    {
        var targetScenarios = CheckAvailableTargets(context.RegisteredScenarios, GetTargetScenarios(context));
        if (targetScenarios.IsError) return Result<SessionArgs>.Fail(targetScenarios.Error);

        var reportName = CheckReportName(GetReportFileNameOrDefault(DateTime.UtcNow, context));
        if (reportName.IsError) return Result<SessionArgs>.Fail(reportName.Error);

        var reportFolder = CheckReportFolder(GetReportFolderOrDefault(testInfo.SessionId, context));
        if (reportFolder.IsError) return Result<SessionArgs>.Fail(reportFolder.Error);

        var reportingInterval = GetReportingInterval(context);
        if (reportingInterval.IsError) return Result<SessionArgs>.Fail(reportingInterval.Error);

        var scenariosSettings = GetScenariosSettings(context);
        if (scenariosSettings.IsError) return Result<SessionArgs>.Fail(scenariosSettings.Error);

        return Result<SessionArgs>.Ok(new SessionArgs
        {
            TestInfo = testInfo,
            TargetScenarios = targetScenarios.Value,
            ScenariosSettings = scenariosSettings.Value,
            ReportFileName = reportName.Value,
            ReportFolder = reportFolder.Value,
            ReportFormats = GetReportFormats(context),
            ReportingInterval = reportingInterval.Value,
            EnableHintsAnalyzer = GetEnableHintsAnalyzer(context),
            EnableStopTestForcibly = GetEnableStopTestForcibly(context)
        });
    }

    public static Result<List<RuntimeScenario>> CreateScenarios(AutobahnContext context) =>
        ScenarioFactory.CreateScenarios(context.RegisteredScenarios);

    public static IBaseContext CreateBaseContext(TestInfo testInfo, Func<HostInfo> getHostInfo, ILogger logger) =>
        new BaseContext(testInfo, getHostInfo, logger);

    private sealed class BaseContext(TestInfo testInfo, Func<HostInfo> getHostInfo, ILogger logger) : IBaseContext
    {
        public TestInfo TestInfo => testInfo;
        public ILogger Logger => logger;
        public HostInfo GetHostInfo() => getHostInfo();
    }
}
