namespace Autobahn.Internal.Domain;

/// <summary>
/// A validated scenario, ready to run: the user's <see cref="ScenarioProps"/> with its load
/// plan expanded onto a timeline and its durations resolved.
/// </summary>
internal sealed record RuntimeScenario
{
    public required string ScenarioName { get; init; }
    public Func<IScenarioInitContext, Task>? Init { get; init; }
    public Func<IScenarioInitContext, Task>? Clean { get; init; }
    public Func<IScenarioContext, Task<IResponse>>? Run { get; init; }
    public required IReadOnlyList<SimulationPlanItem> LoadSimulations { get; init; }
    public TimeSpan? WarmUpDuration { get; init; }
    public required TimeSpan PlanedDuration { get; init; }

    /// <summary>Set once the scenario stops; null while it is still running.</summary>
    public TimeSpan? ExecutedDuration { get; init; }

    public required string CustomSettings { get; init; }
    public required bool IsInitialized { get; init; }
    public required bool RestartIterationOnFail { get; init; }
    public required int MaxFailCount { get; init; }

    public TimeSpan GetExecutedDuration() => ExecutedDuration ?? PlanedDuration;
}
