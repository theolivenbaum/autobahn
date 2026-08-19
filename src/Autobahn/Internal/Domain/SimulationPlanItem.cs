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
}
