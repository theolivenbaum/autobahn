using System.Globalization;

namespace Autobahn.Thresholds;

/// <summary>
/// One pass/fail rule, checked on every reporting interval and again at the end of the run.
/// </summary>
/// <remarks>
/// A threshold is what makes a load test usable as a CI gate: 4.1.2 could say what happened,
/// but not whether it was acceptable. Build one with the factories on this type and narrow it
/// with the fluent methods in <see cref="ThresholdExtensions"/>.
/// </remarks>
public sealed record Threshold
{
    /// <summary>What kind of thing this rule is about.</summary>
    public required ThresholdScope Scope { get; init; }

    /// <summary>The number the rule reads.</summary>
    public required ThresholdSubject Subject { get; init; }

    public required ThresholdComparison Comparison { get; init; }

    /// <summary>What the observed value is held against.</summary>
    public required double Value { get; init; }

    /// <summary>Null applies the rule to every scenario in the run.</summary>
    public string? ScenarioName { get; init; }

    /// <summary>The step this rule is about, for <see cref="ThresholdScope.Step"/>.</summary>
    public string? StepName { get; init; }

    /// <summary>The code this rule is about, for <see cref="ThresholdScope.StatusCode"/>.</summary>
    public string? StatusCode { get; init; }

    /// <summary>The metric this rule is about, for <see cref="ThresholdScope.Metric"/>.</summary>
    public string? MetricName { get; init; }

    /// <summary>
    /// How long into the run checking starts, so ramp-up noise does not trip a rule about the
    /// steady state. Null starts checking at the first reporting interval.
    /// </summary>
    public TimeSpan? StartsAfter { get; init; }

    /// <summary>
    /// How many consecutive failed checks end the run. Null is advisory: the rule is recorded,
    /// reported and fails the run at the end, but the load keeps going.
    /// </summary>
    public int? AbortAfter { get; init; }

    /// <summary>
    /// Checks this rule only once, against the whole run, rather than on every reporting
    /// interval. Cumulative claims need it: "at least 10,000 requests" is true of the run and
    /// false of every interval in it.
    /// </summary>
    public bool FinalOnly { get; init; }

    /// <summary>Overrides the generated description shown in the reports.</summary>
    public string? Name { get; init; }

    // Scenario and step rules.

    /// <summary>The common case: the error rate must stay under this share, 0 to 1.</summary>
    public static Threshold ErrorRateBelow(double rate) =>
        ErrorRate(ThresholdComparison.LessThan, rate);

    /// <summary>The common case: this latency figure, in milliseconds, must stay under a ceiling.</summary>
    public static Threshold LatencyBelow(ThresholdSubject subject, double milliseconds) =>
        Latency(subject, ThresholdComparison.LessThan, milliseconds);

    /// <summary>The common case: throughput must hold above a floor.</summary>
    public static Threshold RpsAbove(double rps) =>
        Rps(ThresholdComparison.GreaterThan, rps);

    public static Threshold ErrorRate(ThresholdComparison comparison, double value) =>
        Stat(ThresholdSubject.ErrorRate, comparison, value);

    public static Threshold OkRate(ThresholdComparison comparison, double value) =>
        Stat(ThresholdSubject.OkRate, comparison, value);

    public static Threshold Latency(ThresholdSubject subject, ThresholdComparison comparison, double milliseconds) =>
        Stat(subject, comparison, milliseconds);

    public static Threshold Rps(ThresholdComparison comparison, double value) =>
        Stat(ThresholdSubject.Rps, comparison, value);

    /// <summary>A rule about a scenario's own totals. Narrow it to a step with <c>ForStep</c>.</summary>
    public static Threshold Stat(ThresholdSubject subject, ThresholdComparison comparison, double value) =>
        new() { Scope = ThresholdScope.Scenario, Subject = subject, Comparison = comparison, Value = value };

    /// <summary>A rule about how often one status code came back.</summary>
    public static Threshold Status(
        string statusCode, ThresholdSubject subject, ThresholdComparison comparison, double value) =>
        new()
        {
            Scope = ThresholdScope.StatusCode,
            StatusCode = statusCode,
            Subject = subject,
            Comparison = comparison,
            Value = value
        };

    /// <summary>A rule about one of the run's metrics.</summary>
    public static Threshold Metric(
        string metricName, ThresholdSubject subject, ThresholdComparison comparison, double value) =>
        new()
        {
            Scope = ThresholdScope.Metric,
            MetricName = metricName,
            Subject = subject,
            Comparison = comparison,
            Value = value
        };

    /// <summary>What the reports call this rule.</summary>
    public string Describe()
    {
        if (!string.IsNullOrWhiteSpace(Name)) return Name;

        var target = Scope switch
        {
            ThresholdScope.Step => $"{ScenarioName ?? "*"}.{StepName}",
            ThresholdScope.StatusCode => $"{ScenarioName ?? "*"} status {StatusCode}",
            ThresholdScope.Metric => MetricName ?? "?",
            _ => ScenarioName ?? "*"
        };

        var op = Comparison switch
        {
            ThresholdComparison.LessThan => "<",
            ThresholdComparison.LessThanOrEqual => "<=",
            ThresholdComparison.GreaterThan => ">",
            ThresholdComparison.GreaterThanOrEqual => ">=",
            _ => "?"
        };

        return $"{target} {Subject} {op} {Value.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>True when the observed value satisfies the rule.</summary>
    public bool IsSatisfiedBy(double observed) => Comparison switch
    {
        ThresholdComparison.LessThan => observed < Value,
        ThresholdComparison.LessThanOrEqual => observed <= Value,
        ThresholdComparison.GreaterThan => observed > Value,
        ThresholdComparison.GreaterThanOrEqual => observed >= Value,
        _ => throw new NotSupportedException($"Unknown threshold comparison: {Comparison}")
    };
}
