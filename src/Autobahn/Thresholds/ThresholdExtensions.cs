namespace Autobahn.Thresholds;

/// <summary>Narrows a threshold to what it is actually about.</summary>
public static class ThresholdExtensions
{
    /// <summary>Applies the rule to one scenario. Without this it applies to every scenario.</summary>
    public static Threshold ForScenario(this Threshold threshold, string scenarioName) =>
        threshold with { ScenarioName = scenarioName };

    /// <summary>Applies the rule to one step rather than to the scenario's totals.</summary>
    public static Threshold ForStep(this Threshold threshold, string stepName) =>
        threshold with { Scope = ThresholdScope.Step, StepName = stepName };

    /// <summary>
    /// Starts checking only this far into the run, so the ramp does not trip a rule written
    /// about the steady state.
    /// </summary>
    public static Threshold StartingAfter(this Threshold threshold, TimeSpan elapsed) =>
        threshold with { StartsAfter = elapsed };

    /// <summary>
    /// Ends the run once the rule has failed this many consecutive checks. Without it the rule
    /// is advisory: recorded, reported, and it fails the run at the end, but the load carries on.
    /// </summary>
    public static Threshold AbortingAfter(this Threshold threshold, int consecutiveChecks) =>
        threshold with { AbortAfter = consecutiveChecks };

    /// <summary>
    /// Checks the rule once, against the whole run, instead of on every reporting interval.
    /// A cumulative claim needs this: "the run made at least 10,000 requests" is true of the
    /// run and false of every interval in it, so checked per interval it would always fail.
    /// </summary>
    public static Threshold OnlyAtTheEnd(this Threshold threshold) =>
        threshold with { FinalOnly = true };

    /// <summary>Overrides what the reports call this rule.</summary>
    public static Threshold Named(this Threshold threshold, string name) =>
        threshold with { Name = name };
}
