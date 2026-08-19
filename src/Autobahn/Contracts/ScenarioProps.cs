namespace Autobahn;

/// <summary>
/// The configuration of a scenario, as the user built it. Create one with
/// <see cref="Scenario.Create"/> or <see cref="Scenario.Empty"/> and shape it with the
/// <c>With...</c> extension methods.
/// </summary>
public sealed record ScenarioProps
{
    public required string ScenarioName { get; init; }

    /// <summary>Runs once before warm-up. Null when the scenario needs no initialization.</summary>
    public Func<IScenarioInitContext, Task>? Init { get; init; }

    /// <summary>Runs once after the session. Null when the scenario needs no cleanup.</summary>
    public Func<IScenarioInitContext, Task>? Clean { get; init; }

    /// <summary>Runs when this scenario finishes, with its final statistics.</summary>
    public Func<IScenarioCompletionContext, Task>? OnCompleted { get; init; }

    /// <summary>The user flow that Autobahn invokes and measures. Null for an empty scenario.</summary>
    public Func<IScenarioContext, Task<IResponse>>? Run { get; init; }

    /// <summary>Null disables warm-up for this scenario.</summary>
    public TimeSpan? WarmUpDuration { get; init; }

    public required IReadOnlyList<LoadSimulation> LoadSimulations { get; init; }

    /// <summary>When true, a failed step aborts the iteration and restarts it.</summary>
    public required bool RestartIterationOnFail { get; init; }

    /// <summary>How many scenario failures end the whole test.</summary>
    public required int MaxFailCount { get; init; }

    /// <summary>
    /// This scenario's share of the combined load, or null when it carries its plan as
    /// written. Either every scenario in a run declares a weight or none does.
    /// </summary>
    public int? Weight { get; init; }

    /// <summary>
    /// How long in-flight iterations get to finish after the load plan ends before they are
    /// abandoned and left out of the numbers.
    /// </summary>
    public TimeSpan? CompletionTimeout { get; init; }

    /// <summary>
    /// How long one iteration - and each step inside it - may take before it is cancelled and
    /// recorded as a timeout. Null leaves iterations to run as long as they like.
    /// </summary>
    public TimeSpan? IterationTimeout { get; init; }
}
