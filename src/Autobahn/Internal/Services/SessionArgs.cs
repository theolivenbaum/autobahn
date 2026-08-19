using Autobahn.Configuration;
using Autobahn.Stats;
using Autobahn.Thresholds;

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

    /// <summary>The pass/fail rules this run is gated on.</summary>
    public required IReadOnlyList<Threshold> Thresholds { get; init; }

    /// <summary>Whether a failed threshold sets a non-zero process exit code.</summary>
    public required bool EnableThresholdExitCode { get; init; }

    /// <summary>The run-wide CustomSettings block, which each scenario's own overrides.</summary>
    public string GlobalCustomSettings { get; init; } = string.Empty;

    /// <summary>Each resolved setting and the layer its value came from.</summary>
    public IReadOnlyList<EffectiveSetting> EffectiveSettings { get; init; } = [];

    /// <summary>Whether to print the above before the run starts.</summary>
    public bool ShowEffectiveConfig { get; init; }

    /// <summary>Called with each closed interval's numbers, or null.</summary>
    public Func<TimeLineHistoryRecord, Task>? OnInterval { get; init; }

    /// <summary>Called once with the resolved run, before any load is generated, or null.</summary>
    public Func<SessionStartInfo, Task>? OnSessionStart { get; init; }

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
        EnableCancelKeyPress = true,
        Thresholds = [],
        EnableThresholdExitCode = true
    };
}
