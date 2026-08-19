using Autobahn.Stats;

namespace Autobahn.Internal.Domain;

/// <summary>Turns each scheduler's per-interval stats into one timeline the reports can walk.</summary>
internal static class TimeLineHistory
{
    public static TimeLineHistoryRecord[] Create(
        IEnumerable<IReadOnlyDictionary<TimeSpan, ScenarioStats>> schedulersRealtimeStats,
        IReadOnlyDictionary<TimeSpan, MetricStats[]>? intervalMetrics = null,
        IReadOnlyDictionary<TimeSpan, ThresholdResult[]>? intervalThresholds = null) =>
        schedulersRealtimeStats
            .SelectMany(x => x)
            .GroupBy(x => x.Key)
            .Select(g => new TimeLineHistoryRecord
            {
                ScenarioStats = g.Select(x => x.Value).ToArray(),
                Metrics = intervalMetrics?.GetValueOrDefault(g.Key) ?? [],
                Thresholds = intervalThresholds?.GetValueOrDefault(g.Key) ?? [],
                Duration = g.Key
            })
            .OrderBy(x => x.Duration)
            .ToArray();
}
