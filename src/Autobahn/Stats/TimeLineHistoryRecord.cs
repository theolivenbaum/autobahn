namespace Autobahn.Stats;

/// <summary>One reporting interval's worth of stats, for every scenario that was running.</summary>
public sealed record TimeLineHistoryRecord
{
    public required ScenarioStats[] ScenarioStats { get; init; }

    /// <summary>What the metrics did over this interval, ordered by name.</summary>
    public MetricStats[] Metrics { get; init; } = [];

    /// <summary>
    /// Where each threshold stood when this interval was checked.
    /// </summary>
    /// <remarks>
    /// Carried on the interval rather than only in the final stats, because a threshold that
    /// passed, failed for a minute and recovered is a different run from one that failed at
    /// the end - and the timeline is the only place that difference exists. Empty when the run
    /// declared no thresholds.
    /// </remarks>
    public ThresholdResult[] Thresholds { get; init; } = [];

    public required TimeSpan Duration { get; init; }
}
