namespace Autobahn.Internal;

/// <summary>
/// Something wrong with a scenario's load plan.
/// </summary>
/// <remarks>
/// Every one of these names the scenario it came from. With several scenarios registered,
/// an unattributed "LoadSimulation duration is smaller than min" is a guessing game.
/// </remarks>
internal abstract record LoadSimulationError : AppError
{
    public sealed record EmptySimulationsList(string ScenarioName) : LoadSimulationError
    {
        public override string Message =>
            $"Scenario '{ScenarioName}' has an empty LoadSimulations list. A scenario needs at least "
            + "one load simulation to run.";
    }

    public sealed record DurationIsSmallerThanMin(string ScenarioName, LoadSimulation Simulation) : LoadSimulationError
    {
        public override string Message =>
            $"Scenario '{ScenarioName}' has a load simulation '{Simulation}' whose duration is smaller than "
            + $"the min duration value: '{Constants.MinSimulationDuration:hh\\:mm\\:ss}'";
    }

    public sealed record IntervalIsBiggerThanDuration(string ScenarioName, LoadSimulation Simulation) : LoadSimulationError
    {
        public override string Message =>
            $"Scenario '{ScenarioName}' has a load simulation '{Simulation}' whose interval is bigger than "
            + "its duration. The interval should be smaller than the simulation duration.";
    }

    public sealed record CopiesCountIsNegative(string ScenarioName, LoadSimulation Simulation) : LoadSimulationError
    {
        public override string Message =>
            $"Scenario '{ScenarioName}' has a load simulation '{Simulation}' with an invalid copies count. "
            + "The value should be bigger or equal 0";
    }

    public sealed record RateIsNegative(string ScenarioName, LoadSimulation Simulation) : LoadSimulationError
    {
        public override string Message =>
            $"Scenario '{ScenarioName}' has a load simulation '{Simulation}' with an invalid rate. "
            + "The value should be bigger or equal 0";
    }

    public sealed record IterationsCountIsNotPositive(string ScenarioName, LoadSimulation Simulation) : LoadSimulationError
    {
        public override string Message =>
            $"Scenario '{ScenarioName}' has a load simulation '{Simulation}' with an invalid iterations count. "
            + "The value should be bigger than 0";
    }

    public sealed record IntervalIsNotPositive(string ScenarioName, LoadSimulation Simulation) : LoadSimulationError
    {
        public override string Message =>
            $"Scenario '{ScenarioName}' has a load simulation '{Simulation}' with an injection interval of zero "
            + "or less. The interval is how often copies are injected, so it has to be a positive duration.";
    }

    /// <summary>
    /// A random injection whose bounds do not straddle anything is not random, and almost
    /// always a typo rather than an intention.
    /// </summary>
    public sealed record RandomRatesAreNotAscending(string ScenarioName, LoadSimulation Simulation) : LoadSimulationError
    {
        public override string Message =>
            $"Scenario '{ScenarioName}' has a load simulation '{Simulation}' whose minRate is not smaller than "
            + "its maxRate. InjectRandom picks a rate between the two bounds, so minRate has to be below maxRate; "
            + "for a fixed rate use Inject instead.";
    }
}
