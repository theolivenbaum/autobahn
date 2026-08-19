using System.Data;

namespace Autobahn.Stats;

/// <summary>
/// The final statistics for a whole session: every scenario, plus what the plugins
/// contributed and which report files were written.
/// </summary>
public sealed record SessionStats
{
    public required ScenarioStats[] ScenarioStats { get; init; }
    public required DataSet[] PluginStats { get; init; }
    public required HostInfo HostInfo { get; init; }
    public required TestInfo TestInfo { get; init; }
    public required ReportFile[] ReportFiles { get; init; }
    public required int AllRequestCount { get; init; }
    public required int AllOkCount { get; init; }
    public required int AllFailCount { get; init; }
    public required long AllBytes { get; init; }
    public required TimeSpan Duration { get; init; }

    public static SessionStats Empty { get; } = new()
    {
        ScenarioStats = [],
        PluginStats = [],
        HostInfo = HostInfo.Empty,
        TestInfo = TestInfo.Empty,
        ReportFiles = [],
        AllRequestCount = 0,
        AllOkCount = 0,
        AllFailCount = 0,
        AllBytes = 0,
        Duration = TimeSpan.Zero
    };

    /// <summary>Finds one scenario's stats by name, or throws if the session has no such scenario.</summary>
    public ScenarioStats GetScenarioStats(string scenarioName) =>
        ScenarioStats.FirstOrDefault(x => x.ScenarioName == scenarioName)
        ?? throw new KeyNotFoundException($"Scenario: '{scenarioName}' is not found in the session stats");
}
