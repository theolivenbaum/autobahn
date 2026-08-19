namespace Autobahn.Stats;

/// <summary>Everything measured for one scenario over one window.</summary>
public sealed record ScenarioStats
{
    public required string ScenarioName { get; init; }
    public required MeasurementStats Ok { get; init; }
    public required MeasurementStats Fail { get; init; }
    public required StepStats[] StepStats { get; init; }
    public required LoadSimulationStats LoadSimulationStats { get; init; }
    public required OperationType CurrentOperation { get; init; }
    public required int AllRequestCount { get; init; }
    public required int AllOkCount { get; init; }
    public required int AllFailCount { get; init; }
    public required long AllBytes { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>Finds one step's stats by name, or throws if this scenario has no such step.</summary>
    public StepStats GetStepStats(string stepName) =>
        StepStats.FirstOrDefault(x => x.StepName == stepName)
        ?? throw new KeyNotFoundException($"Step: '{stepName}' is not found in Scenario: '{ScenarioName}'");
}
