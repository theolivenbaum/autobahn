using Autobahn.Configuration;
using Autobahn.Stats;

namespace Autobahn.Internal.Services;

/// <summary>
/// The fully resolved settings for one session.
/// </summary>
/// <remarks>
/// Every value here has already been merged from code defaults, the JSON config and the CLI
/// arguments, so nothing downstream has to know where a value came from or deal with a
/// missing one. The fork point carried the half-merged config around instead, which is why
/// reading a single setting there meant unwrapping three nested options.
/// </remarks>
internal sealed record SessionArgs
{
    public required TestInfo TestInfo { get; init; }
    public required IReadOnlyList<string> TargetScenarios { get; init; }
    public required IReadOnlyList<ScenarioSetting> ScenariosSettings { get; init; }
    public required string ReportFileName { get; init; }
    public required string ReportFolder { get; init; }
    public required IReadOnlyList<ReportFormat> ReportFormats { get; init; }
    public required TimeSpan ReportingInterval { get; init; }
    public required bool EnableHintsAnalyzer { get; init; }
    public required bool EnableStopTestForcibly { get; init; }

    /// <summary>Cancelling this ends the run early; the reports are still written.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Whether Ctrl+C is turned into an early stop rather than killing the process.</summary>
    public required bool EnableCancelKeyPress { get; init; }

    public static SessionArgs Empty { get; } = new()
    {
        TestInfo = TestInfo.Empty,
        TargetScenarios = [],
        ScenariosSettings = [],
        ReportFileName = Constants.DefaultReportName,
        ReportFolder = Constants.DefaultReportFolder,
        ReportFormats = Constants.AllReportFormats,
        ReportingInterval = Constants.DefaultReportingInterval,
        EnableHintsAnalyzer = false,
        EnableStopTestForcibly = false,
        CancellationToken = default,
        EnableCancelKeyPress = true
    };
}
