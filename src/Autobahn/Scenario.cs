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
}
