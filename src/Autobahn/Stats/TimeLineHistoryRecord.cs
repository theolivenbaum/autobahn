namespace Autobahn.Stats;

/// <summary>One reporting interval's worth of stats, for every scenario that was running.</summary>
public sealed record TimeLineHistoryRecord
{
    public required ScenarioStats[] ScenarioStats { get; init; }
    public required TimeSpan Duration { get; init; }
}
