namespace Autobahn.Internal;

/// <summary>Something wrong with a scenario's load plan.</summary>
internal abstract record LoadSimulationError : AppError
{
    public sealed record EmptySimulationsList : LoadSimulationError
    {
        public override string Message => "LoadSimulations list is empty";
    }

    public sealed record DurationIsSmallerThanMin(LoadSimulation Simulation) : LoadSimulationError
    {
        public override string Message =>
            $"LoadSimulation duration: '{Simulation}' is smaller than min duration value: "
            + $"'{Constants.MinSimulationDuration:hh\\:mm\\:ss}'";
    }

    public sealed record IntervalIsBiggerThanDuration(LoadSimulation Simulation) : LoadSimulationError
    {
        public override string Message =>
            $"LoadSimulation interval: '{Simulation}' should be smaller than simulation duration";
    }

    public sealed record CopiesCountIsNegative(LoadSimulation Simulation) : LoadSimulationError
    {
        public override string Message =>
            $"LoadSimulation: '{Simulation}' has invalid copiesCount value. The value should be bigger or equal 0";
    }

    public sealed record RateIsNegative(LoadSimulation Simulation) : LoadSimulationError
    {
        public override string Message =>
            $"LoadSimulation: '{Simulation}' has invalid rate value. The value should be bigger or equal 0";
    }
}
