using Autobahn.Stats;

namespace Autobahn.Internal.Domain;

/// <summary>Turns each scheduler's per-interval stats into one timeline the reports can walk.</summary>
internal static class TimeLineHistory
{
    public static TimeLineHistoryRecord[] Create(IEnumerable<IReadOnlyDictionary<TimeSpan, ScenarioStats>> schedulersRealtimeStats) =>
        schedulersRealtimeStats
            .SelectMany(x => x)
            .GroupBy(x => x.Key)
            .Select(g => new TimeLineHistoryRecord
            {
                ScenarioStats = g.Select(x => x.Value).ToArray(),
                Duration = g.Key
            })
            .OrderBy(x => x.Duration)
            .ToArray();
}
