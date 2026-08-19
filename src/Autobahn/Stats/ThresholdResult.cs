using Autobahn.Thresholds;

namespace Autobahn.Stats;

/// <summary>How one threshold fared over a run.</summary>
public sealed record ThresholdResult
{
    /// <summary>What the reports call this rule.</summary>
    public required string Name { get; init; }

    public required ThresholdScope Scope { get; init; }
    public required ThresholdSubject Subject { get; init; }
    public required ThresholdComparison Comparison { get; init; }

    /// <summary>What the rule asked for.</summary>
    public required double Value { get; init; }

    /// <summary>The scenario this result is about, or empty when the rule is not scenario-scoped.</summary>
    public required string ScenarioName { get; init; }

    /// <summary>The last value the rule saw.</summary>
    public required double ObservedValue { get; init; }

    public required bool Passed { get; init; }

    /// <summary>How far into the run the rule first failed, or null if it never did.</summary>
    public TimeSpan? FirstFailedAt { get; init; }

    public required int FailedChecks { get; init; }
    public required int TotalChecks { get; init; }

    /// <summary>True when this rule is what ended the run.</summary>
    public required bool Aborted { get; init; }
}
