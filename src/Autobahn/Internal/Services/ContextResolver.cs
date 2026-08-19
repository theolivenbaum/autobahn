using Autobahn.Configuration;
using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Thresholds;
using Autobahn.Metrics;
using Autobahn.Stats;
using Autobahn.Thresholds;
using Microsoft.Extensions.Logging;

namespace Autobahn.Internal.Services;

/// <summary>
/// Works out the effective value of every setting, given the code defaults and the JSON
/// config, and validates the result.
/// </summary>
/// <remarks>
/// Precedence, weakest to strongest: Autobahn's own defaults, then anything set in code, then
/// the JSON config, then <c>AUTOBAHN_</c> environment variables, then command-line arguments.
/// Every <c>Get…</c> below follows that order, and the source that won is recorded in a
/// <see cref="ProvenanceLog"/> as it goes - so "why is the warm-up thirty seconds" is
/// answerable from the run itself rather than by reading three files.
/// </remarks>
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

    public static string GetTestSuite(AutobahnContext context, ProvenanceLog? log = null) =>
        EnvironmentConfig.TestSuite is { } fromEnv
            ? Log(log, "TestSuite", fromEnv, ConfigSource.Environment)
            : context.Config?.TestSuite is { } fromJson
                ? Log(log, "TestSuite", fromJson, ConfigSource.JsonConfig)
                : Log(log, "TestSuite", context.TestSuite, SourceOfCoded(context.TestSuite, Constants.DefaultTestSuite));

    public static string GetTestName(AutobahnContext context, ProvenanceLog? log = null) =>
        EnvironmentConfig.TestName is { } fromEnv
            ? Log(log, "TestName", fromEnv, ConfigSource.Environment)
            : context.Config?.TestName is { } fromJson
                ? Log(log, "TestName", fromJson, ConfigSource.JsonConfig)
                : Log(log, "TestName", context.TestName, SourceOfCoded(context.TestName, Constants.DefaultTestName));

    private static T Log<T>(ProvenanceLog? log, string name, T value, ConfigSource source) =>
        log is null ? value : log.Record(name, value, source);

    /// <summary>Tells a value someone chose apart from the one Autobahn shipped with.</summary>
    private static ConfigSource SourceOfCoded<T>(T value, T defaultValue) =>
        EqualityComparer<T>.Default.Equals(value, defaultValue) ? ConfigSource.Default : ConfigSource.Code;

    public static Result<IReadOnlyList<ScenarioSetting>> GetScenariosSettings(AutobahnContext context)
    {
        var settings = Global(context)?.ScenariosSettings;

        return settings is null
            ? Result<IReadOnlyList<ScenarioSetting>>.Ok([])
            : CheckDuplicateScenarioSettings(settings);
    }

    /// <summary>
    /// Which scenarios the run targets. Unlike every other setting, an explicit list set in
    /// code wins over the JSON config: <c>WithTargetScenarios</c> is also how the command line
    /// narrows a run, and a config file that named a wider set must not widen it back.
    /// </summary>
    public static IReadOnlyList<string> GetTargetScenarios(AutobahnContext context, ProvenanceLog? log = null) =>
        context.TargetScenarios is { } fromCode
            ? Log(log, "TargetScenarios", fromCode, ConfigSource.Code)
            : EnvironmentConfig.TargetScenarios is { } fromEnv
                ? Log(log, "TargetScenarios", fromEnv, ConfigSource.Environment)
                : context.Config?.TargetScenarios is { } fromJson
                    ? Log(log, "TargetScenarios", fromJson, ConfigSource.JsonConfig)
                    : Log(log, "TargetScenarios",
                        (IReadOnlyList<string>)context.RegisteredScenarios.Select(x => x.ScenarioName).ToList(),
                        ConfigSource.Default);

    public static AutobahnContext SetTargetScenarios(IReadOnlyList<string> scenarios, AutobahnContext context) =>
        context with { TargetScenarios = scenarios };

    public static string? GetReportFileName(AutobahnContext context) =>
        EnvironmentConfig.ReportFileName ?? Global(context)?.ReportFileName ?? context.Reporting.FileName;

    public static string GetReportFileNameOrDefault(
        DateTime currentTime, AutobahnContext context, ProvenanceLog? log = null)
    {
        if (EnvironmentConfig.ReportFileName is { } fromEnv)
            return Log(log, "ReportFileName", fromEnv, ConfigSource.Environment);

        if (Global(context)?.ReportFileName is { } fromJson)
            return Log(log, "ReportFileName", fromJson, ConfigSource.JsonConfig);

        if (context.Reporting.FileName is { } fromCode)
            return Log(log, "ReportFileName", fromCode, ConfigSource.Code);

        return Log(log, "ReportFileName",
            $"{Constants.DefaultReportName}_{currentTime:yyyy-MM-dd--HH-mm-ss}", ConfigSource.Default);
    }

    public static bool GetEnableHintsAnalyzer(AutobahnContext context, ProvenanceLog? log = null) =>
        EnvironmentConfig.EnableHintsAnalyzer is { } fromEnv
            ? Log(log, "EnableHintsAnalyzer", fromEnv, ConfigSource.Environment)
            : Global(context)?.EnableHintsAnalyzer is { } fromJson
                ? Log(log, "EnableHintsAnalyzer", fromJson, ConfigSource.JsonConfig)
                : Log(log, "EnableHintsAnalyzer", context.EnableHintsAnalyzer,
                    SourceOfCoded(context.EnableHintsAnalyzer, false));

    public static bool GetEnableStopTestForcibly(AutobahnContext context) =>
        Global(context)?.EnableStopTestForcibly ?? context.EnableStopTestForcibly;

    /// <summary>
    /// The rules the run is gated on: what the code declared, plus what the JSON config added.
    /// </summary>
    /// <remarks>
    /// Config thresholds add to the code's rather than replacing them, because the two answer
    /// different questions - the code says what the test is always about, and the config says
    /// what this environment additionally demands. A rule under a scenario's settings block
    /// takes that scenario's name from the block it sits in, so it need not repeat it.
    /// </remarks>
    public static IReadOnlyList<Threshold> GetThresholds(AutobahnContext context)
    {
        var global = Global(context)?.Thresholds ?? [];

        var perScenario = (Global(context)?.ScenariosSettings ?? [])
            .Where(setting => setting.Thresholds is not null)
            .SelectMany(setting => setting.Thresholds!.Select(x => x.ScenarioName is null
                ? x with { ScenarioName = setting.ScenarioName }
                : x));

        return [.. context.Thresholds, .. global, .. perScenario];
    }

    public static string GetReportFolderOrDefault(
        string sessionId, AutobahnContext context, ProvenanceLog? log = null)
    {
        if (EnvironmentConfig.ReportFolder is { } fromEnv)
            return Log(log, "ReportFolder", fromEnv, ConfigSource.Environment);

        if (Global(context)?.ReportFolder is { } fromJson)
            return Log(log, "ReportFolder", fromJson, ConfigSource.JsonConfig);

        if (context.Reporting.FolderName is { } fromCode)
            return Log(log, "ReportFolder", fromCode, ConfigSource.Code);

        return Log(log, "ReportFolder", Path.Combine(Constants.DefaultReportFolder, sessionId), ConfigSource.Default);
    }

    public static IReadOnlyList<ReportFormat> GetReportFormats(AutobahnContext context, ProvenanceLog? log = null) =>
        EnvironmentConfig.ReportFormats is { } fromEnv
            ? Log(log, "ReportFormats", fromEnv, ConfigSource.Environment)
            : Global(context)?.ReportFormats is { } fromJson
                ? Log(log, "ReportFormats", fromJson, ConfigSource.JsonConfig)
                : Log(log, "ReportFormats", context.Reporting.Formats,
                    context.Reporting.Formats.SequenceEqual(Constants.AllReportFormats)
                        ? ConfigSource.Default
                        : ConfigSource.Code);

    public static Result<TimeSpan> GetReportingInterval(AutobahnContext context, ProvenanceLog? log = null)
    {
        var (value, source) =
            EnvironmentConfig.ReportingInterval is { } fromEnv ? (fromEnv, ConfigSource.Environment)
            : Global(context)?.ReportingInterval is { } fromJson ? (fromJson, ConfigSource.JsonConfig)
            : (context.Reporting.ReportingInterval,
                SourceOfCoded(context.Reporting.ReportingInterval, Constants.DefaultReportingInterval));

        var checkedValue = CheckReportingInterval(value);
        if (checkedValue.IsOk) Log(log, "ReportingInterval", checkedValue.Value, source);

        return checkedValue;
    }

    public static Result<SessionArgs> CreateSessionArgs(
        TestInfo testInfo, AutobahnContext context, ProvenanceLog? provenance = null)
    {
        // The session's own name and suite are resolved before this, to build the TestInfo, so
        // the caller passes the log it recorded them into rather than starting a fresh one.
        var log = provenance ?? new ProvenanceLog();

        if (provenance is null)
        {
            GetTestSuite(context, log);
            GetTestName(context, log);
        }

        var targetScenarios = CheckAvailableTargets(context.RegisteredScenarios, GetTargetScenarios(context, log));
        if (targetScenarios.IsError) return Result<SessionArgs>.Fail(targetScenarios.Error);

        var reportName = CheckReportName(GetReportFileNameOrDefault(DateTime.UtcNow, context, log));
        if (reportName.IsError) return Result<SessionArgs>.Fail(reportName.Error);

        var reportFolder = CheckReportFolder(GetReportFolderOrDefault(testInfo.SessionId, context, log));
        if (reportFolder.IsError) return Result<SessionArgs>.Fail(reportFolder.Error);

        var reportingInterval = GetReportingInterval(context, log);
        if (reportingInterval.IsError) return Result<SessionArgs>.Fail(reportingInterval.Error);

        var scenariosSettings = GetScenariosSettings(context);
        if (scenariosSettings.IsError) return Result<SessionArgs>.Fail(scenariosSettings.Error);

        // Validated against the whole registered set rather than the target subset: a rule
        // about a scenario this run did not target is a narrowed run, not a typo.
        var thresholds = ThresholdValidation.Check(
            GetThresholds(context), context.RegisteredScenarios.Select(x => x.ScenarioName).ToArray());

        if (thresholds.IsError) return Result<SessionArgs>.Fail(thresholds.Error);

        return Result<SessionArgs>.Ok(new SessionArgs
        {
            TestInfo = testInfo,
            TargetScenarios = targetScenarios.Value,
            ScenariosSettings = scenariosSettings.Value,
            ReportFileName = reportName.Value,
            ReportFolder = reportFolder.Value,
            ReportFormats = GetReportFormats(context, log),
            ReportingInterval = reportingInterval.Value,
            EnableHintsAnalyzer = GetEnableHintsAnalyzer(context, log),
            EnableStopTestForcibly = GetEnableStopTestForcibly(context),
            CancellationToken = context.CancellationToken,
            EnableCancelKeyPress = context.EnableCancelKeyPress,
            Thresholds = thresholds.Value,
            GlobalCustomSettings = Global(context)?.CustomSettings ?? string.Empty,
            EffectiveSettings = log.Settings,
            ShowEffectiveConfig = context.ShowEffectiveConfig,
            OnInterval = context.OnInterval,
            OnSessionStart = context.OnSessionStart,
            EnableThresholdExitCode = context.EnableThresholdExitCode
        });
    }

    public static Result<List<RuntimeScenario>> CreateScenarios(AutobahnContext context) =>
        ScenarioFactory.CreateScenarios(context.RegisteredScenarios);

    public static IBaseContext CreateBaseContext(
        TestInfo testInfo, Func<HostInfo> getHostInfo, ILogger logger, IMetricRegistry metrics) =>
        new BaseContext(testInfo, getHostInfo, logger, metrics);

    private sealed class BaseContext(
        TestInfo testInfo, Func<HostInfo> getHostInfo, ILogger logger, IMetricRegistry metrics) : IBaseContext
    {
        public TestInfo TestInfo => testInfo;
        public ILogger Logger => logger;
        public HostInfo GetHostInfo() => getHostInfo();
        public IMetricRegistry Metrics => metrics;
    }
}
