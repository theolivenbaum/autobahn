namespace Autobahn.Thresholds;

/// <summary>
/// The number a threshold reads. Which subjects make sense depends on the
/// <see cref="ThresholdScope"/>; a mismatch is rejected up front rather than silently
/// comparing against zero.
/// </summary>
public enum ThresholdSubject
{
    // Scenario and step.

    /// <summary>Failed requests as a share of all requests, 0 to 1.</summary>
    ErrorRate,

    /// <summary>Successful requests as a share of all requests, 0 to 1.</summary>
    OkRate,

    RequestCount,
    OkCount,
    FailCount,

    /// <summary>Successful requests per second.</summary>
    Rps,

    MinLatency,
    MeanLatency,
    MaxLatency,
    Percent50,
    Percent75,
    Percent95,
    Percent99,

    /// <summary>Total bytes transferred, over successful requests.</summary>
    AllBytes,

    // Status code.

    /// <summary>How many responses carried the code.</summary>
    StatusCodeCount,

    /// <summary>The code's share of all the scenario's requests, 0 to 1.</summary>
    StatusCodeRate,

    // Metric.

    /// <summary>A counter's total, a gauge's latest value, a histogram's last recording.</summary>
    MetricCurrent,

    MetricMin,
    MetricMean,
    MetricMax,
    MetricPercent50,
    MetricPercent95,
    MetricPercent99,

    /// <summary>How many writes the metric saw.</summary>
    MetricCount
}
