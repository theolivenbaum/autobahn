using Autobahn.Stats;

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

    public static GlobalSettings Empty { get; } = new();
}
