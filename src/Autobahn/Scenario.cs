namespace Autobahn;

/// <summary>
/// A scenario is the workflow a virtual user follows. Create one, shape it with the
/// <c>With...</c> methods, and register it with <see cref="AutobahnRunner"/>.
/// </summary>
public static class Scenario
{
    /// <summary>Creates a scenario from the user flow Autobahn should invoke and measure.</summary>
    public static ScenarioProps Create(string name, Func<IScenarioContext, Task<IResponse>> run) => new()
    {
        ScenarioName = name,
        Init = null,
        Clean = null,
        Run = run,
        WarmUpDuration = Constants.DefaultWarmUpDuration,
        CompletionTimeout = Constants.DefaultCompletionTimeout,
        LoadSimulations = [Simulation.KeepConstant(Constants.DefaultCopiesCount, Constants.DefaultSimulationDuration)],
        RestartIterationOnFail = true,
        MaxFailCount = Constants.ScenarioMaxFailCount
    };

    /// <summary>
    /// Creates a scenario that runs no load - only its init and clean. Useful when several
    /// scenarios share setup that should happen exactly once.
    /// </summary>
    public static ScenarioProps Empty(string name) => new()
    {
        ScenarioName = name,
        Init = null,
        Clean = null,
        Run = null,
        WarmUpDuration = null,
        CompletionTimeout = Constants.DefaultCompletionTimeout,
        LoadSimulations = [Simulation.KeepConstant(Constants.DefaultCopiesCount, Constants.DefaultSimulationDuration)],
        RestartIterationOnFail = true,
        MaxFailCount = Constants.ScenarioMaxFailCount
    };

    /// <summary>
    /// Prepares the scenario and its dependencies before warm-up. If init throws, the run stops.
    /// </summary>
    public static ScenarioProps WithInit(this ScenarioProps scenario, Func<IScenarioInitContext, Task> initFunc) =>
        scenario with { Init = initFunc };

    /// <summary>
    /// Releases the scenario's resources after the session. If clean throws, Autobahn logs it
    /// and carries on.
    /// </summary>
    public static ScenarioProps WithClean(this ScenarioProps scenario, Func<IScenarioInitContext, Task> cleanFunc) =>
        scenario with { Clean = cleanFunc };

    /// <summary>Sets how long the warm-up phase runs. The default is 30 seconds.</summary>
    public static ScenarioProps WithWarmUpDuration(this ScenarioProps scenario, TimeSpan duration) =>
        scenario with { WarmUpDuration = duration };

    /// <summary>Skips warm-up for this scenario.</summary>
    public static ScenarioProps WithoutWarmUp(this ScenarioProps scenario) =>
        scenario with { WarmUpDuration = null };

    /// <summary>
    /// Sets the load plan. The default is a single <c>KeepConstant(copies: 1, during: 1 minute)</c>.
    /// </summary>
    public static ScenarioProps WithLoadSimulations(this ScenarioProps scenario, params LoadSimulation[] loadSimulations) =>
        scenario with { LoadSimulations = loadSimulations };

    /// <summary>
    /// Controls whether a failed step aborts the iteration and restarts it. Turn it off when
    /// the scenario handles failures itself - retries, fallbacks, expected error paths.
    /// The default is true.
    /// </summary>
    public static ScenarioProps WithRestartIterationOnFail(this ScenarioProps scenario, bool shouldRestart) =>
        scenario with { RestartIterationOnFail = shouldRestart };

    /// <summary>
    /// How many failed iterations end the whole test. Counts scenario failures, not step
    /// failures. The default is 5,000.
    /// </summary>
    public static ScenarioProps WithMaxFailCount(this ScenarioProps scenario, int maxFailCount) =>
        scenario with { MaxFailCount = maxFailCount };

    /// <summary>
    /// This scenario's share of the combined load, so several scenarios can model one user
    /// population without the author hand-computing rates.
    /// </summary>
    /// <remarks>
    /// Weights are relative: 80 and 20 mean the same as 8 and 2. Each scenario's own plan is
    /// scaled by its share, so give the scenarios in one population the same plan and let the
    /// weights split it. Either every scenario in a run declares a weight or none does.
    /// </remarks>
    public static ScenarioProps WithWeight(this ScenarioProps scenario, int weight) =>
        scenario with { Weight = weight };

    /// <summary>
    /// Runs when this scenario finishes, with its final statistics - the place to push a
    /// result somewhere or fail a build without wrapping the whole runner.
    /// </summary>
    public static ScenarioProps WithCompletionHook(
        this ScenarioProps scenario, Func<IScenarioCompletionContext, Task> onCompleted) =>
        scenario with { OnCompleted = onCompleted };

    /// <summary>
    /// How long in-flight iterations get to finish after the load plan ends. The default is
    /// 10 seconds; iterations still running after it are abandoned and left out of the
    /// numbers, with a warning saying how many.
    /// </summary>
    public static ScenarioProps WithCompletionTimeout(this ScenarioProps scenario, TimeSpan timeout) =>
        scenario with { CompletionTimeout = timeout };

    /// <summary>
    /// Cancels an iteration - and each step inside it - once it has run for this long, and
    /// records it as a timeout rather than as a generic error, so a report can tell "slow"
    /// from "broken". No timeout by default.
    /// </summary>
    public static ScenarioProps WithIterationTimeout(this ScenarioProps scenario, TimeSpan timeout) =>
        scenario with { IterationTimeout = timeout };
}
