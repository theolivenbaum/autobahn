using Autobahn.Stats;
using Autobahn.Thresholds;

namespace Autobahn.Internal.Domain.Thresholds;

/// <summary>
/// One threshold, bound to the scenario it turned out to be about, plus what it has seen.
/// </summary>
/// <remarks>
/// A rule that names no scenario applies to all of them, so one <see cref="Threshold"/> can
/// produce several of these - each with its own tally, because "the error rate stayed under
/// 1%" is a claim about one scenario at a time.
/// </remarks>
internal sealed class ThresholdState(Threshold threshold, string scenarioName)
{
    public Threshold Threshold { get; } = threshold;
    public string ScenarioName { get; } = scenarioName;

    public double ObservedValue { get; private set; }
    public int TotalChecks { get; private set; }
    public int FailedChecks { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public TimeSpan? FirstFailedAt { get; private set; }
    public bool Aborted { get; private set; }

    /// <summary>True once a check has failed, whether or not the rule was allowed to abort.</summary>
    public bool Failed => FailedChecks > 0;

    /// <summary>
    /// Records one check and says whether the rule has now failed often enough in a row to end
    /// the run. A rule with no abort policy never returns true.
    /// </summary>
    public bool Check(double observed, TimeSpan elapsed)
    {
        ObservedValue = observed;
        TotalChecks++;

        if (Threshold.IsSatisfiedBy(observed))
        {
            ConsecutiveFailures = 0;
            return false;
        }

        FailedChecks++;
        ConsecutiveFailures++;
        FirstFailedAt ??= elapsed;

        if (Threshold.AbortAfter is not { } limit || ConsecutiveFailures < limit) return false;

        Aborted = true;
        return true;
    }

    /// <summary>True when the rule should sit this check out.</summary>
    public bool ShouldSkip(TimeSpan elapsed, bool isFinal)
    {
        if (Threshold.FinalOnly && !isFinal) return true;

        return Threshold.StartsAfter is { } start && elapsed < start;
    }

    public ThresholdResult ToResult() => new()
    {
        Name = Threshold.Describe(),
        Scope = Threshold.Scope,
        Subject = Threshold.Subject,
        Comparison = Threshold.Comparison,
        Value = Threshold.Value,
        ScenarioName = ScenarioName,
        ObservedValue = Math.Round(ObservedValue, Constants.StatsRounding),
        // A rule that never got to run - delayed past the end of the run, or about a step or
        // metric that never appeared - has nothing against it, so it passes.
        Passed = !Failed,
        FirstFailedAt = FirstFailedAt,
        FailedChecks = FailedChecks,
        TotalChecks = TotalChecks,
        Aborted = Aborted
    };
}
