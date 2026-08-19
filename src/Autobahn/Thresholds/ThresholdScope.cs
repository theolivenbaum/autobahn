namespace Autobahn.Thresholds;

/// <summary>What a threshold is a rule about.</summary>
public enum ThresholdScope
{
    /// <summary>The scenario's own totals, across all its steps.</summary>
    Scenario,

    /// <summary>One named step inside a scenario.</summary>
    Step,

    /// <summary>One status code's count, or its share of the scenario's requests.</summary>
    StatusCode,

    /// <summary>One named metric.</summary>
    Metric
}
