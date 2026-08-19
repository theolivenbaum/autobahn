using Autobahn.Stats;
using Autobahn.Thresholds;

namespace Autobahn.Internal.Domain.Thresholds;

/// <summary>What a round of threshold checks decided.</summary>
internal readonly record struct ThresholdCheckResult(bool ShouldAbort, IReadOnlyList<string> AbortReasons);

/// <summary>
/// Holds a run's thresholds and checks them, on every reporting interval and again at the end.
/// </summary>
/// <remarks>
/// Checking during the run rather than only after it is the whole point of the abort policy:
/// the difference between a report saying a service was down and not hammering a service that
/// is already down.
/// </remarks>
internal sealed class ThresholdChecker
{
    private readonly List<ThresholdState> _states = [];
    private readonly Lock _sync = new();

    public ThresholdChecker(IReadOnlyList<Threshold> thresholds, IReadOnlyList<string> scenarioNames)
    {
        foreach (var threshold in thresholds)
        {
            // A rule that names no scenario is a rule about each of them, tallied separately:
            // one scenario's error rate says nothing about another's.
            if (threshold.Scope == ThresholdScope.Metric)
            {
                _states.Add(new ThresholdState(threshold, string.Empty));
                continue;
            }

            if (threshold.ScenarioName is { } named)
            {
                _states.Add(new ThresholdState(threshold, named));
                continue;
            }

            foreach (var scenarioName in scenarioNames)
                _states.Add(new ThresholdState(threshold, scenarioName));
        }
    }

    public bool IsEmpty => _states.Count == 0;

    /// <summary>Every scenario a rule mentions that the run does not have.</summary>
    public static IReadOnlyList<string> FindUnknownScenarios(
        IReadOnlyList<Threshold> thresholds, IReadOnlyList<string> scenarioNames) =>
        thresholds
            .Select(x => x.ScenarioName)
            .Where(x => x is not null && !scenarioNames.Contains(x))
            .Select(x => x!)
            .Distinct()
            .ToArray();

    /// <summary>
    /// Checks every rule against this window and says whether one of them has now failed often
    /// enough in a row to end the run. <paramref name="isFinal"/> marks the last check, the one
    /// against the whole run rather than one interval of it.
    /// </summary>
    public ThresholdCheckResult Check(
        TimeSpan elapsed,
        IReadOnlyList<ScenarioStats> scenarioStats,
        IReadOnlyList<MetricStats> metrics,
        bool isFinal = false)
    {
        List<string>? abortReasons = null;

        lock (_sync)
        {
            foreach (var state in _states)
            {
                if (state.Aborted || state.ShouldSkip(elapsed, isFinal)) continue;

                var stats = state.Threshold.Scope == ThresholdScope.Metric
                    ? scenarioStats.FirstOrDefault()
                    : scenarioStats.FirstOrDefault(x => x.ScenarioName == state.ScenarioName);

                if (stats is null) continue;

                var observed = ThresholdSubjectReader.Read(state.Threshold, stats, metrics);
                if (observed is not { } value) continue;

                if (!state.Check(value, elapsed)) continue;

                abortReasons ??= [];
                abortReasons.Add(
                    $"Threshold '{state.Threshold.Describe()}' failed {state.ConsecutiveFailures} checks in a row "
                    + $"(observed {Math.Round(value, Constants.StatsRounding)}).");
            }
        }

        return new ThresholdCheckResult(abortReasons is not null, abortReasons ?? []);
    }

    /// <summary>
    /// The results, ordered by name so a diff between two runs is a diff of verdicts rather
    /// than of row order.
    /// </summary>
    public ThresholdResult[] GetResults()
    {
        lock (_sync)
        {
            return _states
                .Select(x => x.ToResult())
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ThenBy(x => x.ScenarioName, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
