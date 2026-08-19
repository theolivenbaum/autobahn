namespace Autobahn.Stats;

/// <summary>One reporting interval's worth of stats, for every scenario that was running.</summary>
public sealed record TimeLineHistoryRecord
{
    public required ScenarioStats[] ScenarioStats { get; init; }

    /// <summary>What the metrics did over this interval, ordered by name.</summary>
    public MetricStats[] Metrics { get; init; } = [];

    public required TimeSpan Duration { get; init; }
}
