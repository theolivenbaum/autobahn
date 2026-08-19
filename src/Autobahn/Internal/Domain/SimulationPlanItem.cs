namespace Autobahn.Internal.Domain;

/// <summary>
/// One load simulation placed on the scenario's timeline: what it is, when it starts and
/// ends, and how many actors the previous segment left running.
/// </summary>
internal sealed record SimulationPlanItem
{
    public required LoadSimulation Value { get; init; }
    public required TimeSpan StartTime { get; init; }
    public required TimeSpan EndTime { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>The actor count the previous segment ended at, which a ramp interpolates from.</summary>
    public required int PrevActorCount { get; init; }

    /// <summary>
    /// How many iterations this segment runs, or null when it runs for a duration. A segment
    /// with a budget has no duration the plan can know: it ends when the target has taken
    /// that many iterations.
    /// </summary>
    public int? Iterations => Value.IterationCount;

    /// <summary>True when this segment's length is decided at run time rather than by the plan.</summary>
    public bool IsCounted => Iterations is not null;
}
