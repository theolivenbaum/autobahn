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

    /// <summary>The user flow that Autobahn invokes and measures. Null for an empty scenario.</summary>
    public Func<IScenarioContext, Task<IResponse>>? Run { get; init; }

    /// <summary>Null disables warm-up for this scenario.</summary>
    public TimeSpan? WarmUpDuration { get; init; }

    public required IReadOnlyList<LoadSimulation> LoadSimulations { get; init; }

    /// <summary>When true, a failed step aborts the iteration and restarts it.</summary>
    public required bool RestartIterationOnFail { get; init; }

    /// <summary>How many scenario failures end the whole test.</summary>
    public required int MaxFailCount { get; init; }
}
