namespace Autobahn.Stats;

/// <summary>Which load simulation was running, and at what level.</summary>
public sealed record LoadSimulationStats
{
    public required string SimulationName { get; init; }
    public required int Value { get; init; }
}
