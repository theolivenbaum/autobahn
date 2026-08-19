namespace Autobahn.Stats;

/// <summary>Which load simulation was running, and at what level.</summary>
public sealed record LoadSimulationStats
{
    public required string SimulationName { get; init; }

    /// <summary>What the plan asked for: copies for a closed model, injected actors for an open one.</summary>
    public required int Value { get; init; }

    /// <summary>
    /// How many copies were actually mid-iteration when the interval closed.
    /// </summary>
    /// <remarks>
    /// Reported beside <see cref="Value"/> rather than instead of it, because the two
    /// diverging is the clearest sign the generator is saturated rather than the target: a
    /// plan asking for 500 copies while 500 are live is a measurement of the target, and one
    /// asking for 500 while 180 are live is a measurement of this machine.
    /// </remarks>
    public int ActualCopies { get; init; }
}
