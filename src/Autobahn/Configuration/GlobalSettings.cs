using Autobahn.Stats;
using Autobahn.Thresholds;

namespace Autobahn.Configuration;

/// <summary>The session-wide section of the JSON config.</summary>
public sealed record GlobalSettings
{
    public IReadOnlyList<ScenarioSetting>? ScenariosSettings { get; init; }
    public string? ReportFileName { get; init; }
    public string? ReportFolder { get; init; }
    public IReadOnlyList<ReportFormat>? ReportFormats { get; init; }
    public TimeSpan? ReportingInterval { get; init; }
    public bool? EnableHintsAnalyzer { get; init; }
    public bool? EnableStopTestForcibly { get; init; }

    /// <summary>
    /// Run-wide pass/fail rules. Declaring them here rather than in code is what lets one test
    /// binary be gated differently per environment without a recompile.
    /// </summary>
    public IReadOnlyList<Threshold>? Thresholds { get; init; }

    public static GlobalSettings Empty { get; } = new();
}
