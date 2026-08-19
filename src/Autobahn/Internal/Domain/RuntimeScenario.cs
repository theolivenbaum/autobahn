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
    public Func<IScenarioCompletionContext, Task>? OnCompleted { get; init; }
    public required IReadOnlyList<SimulationPlanItem> LoadSimulations { get; init; }
    public TimeSpan? WarmUpDuration { get; init; }
    public required TimeSpan PlanedDuration { get; init; }

    /// <summary>Set once the scenario stops; null while it is still running.</summary>
    public TimeSpan? ExecutedDuration { get; init; }

    /// <summary>This scenario's own CustomSettings block from the config, as raw JSON.</summary>
    public required string CustomSettings { get; init; }

    /// <summary>The run-wide CustomSettings block, which this scenario's own overrides.</summary>
    public string GlobalCustomSettings { get; init; } = string.Empty;
    public required bool IsInitialized { get; init; }
    public required bool RestartIterationOnFail { get; init; }
    public required int MaxFailCount { get; init; }

    /// <summary>This scenario's share of the combined load, or null when it runs its plan as written.</summary>
    public int? Weight { get; init; }

    /// <summary>The most copies this plan ever runs at once. The denominator for partitioning.</summary>
    public required int MaxCopiesCount { get; init; }

    /// <summary>How long to let in-flight iterations finish after the plan ends.</summary>
    public required TimeSpan CompletionTimeout { get; init; }

    /// <summary>Applied to each iteration, and to each step inside it. Null means no timeout.</summary>
    public TimeSpan? IterationTimeout { get; init; }

    /// <summary>
    /// True when at least one segment is counted in iterations, so the plan cannot say how
    /// long the scenario will take.
    /// </summary>
    public bool HasCountedSimulations => LoadSimulations.Any(x => x.IsCounted);

    public TimeSpan GetExecutedDuration() => ExecutedDuration ?? PlanedDuration;
}
