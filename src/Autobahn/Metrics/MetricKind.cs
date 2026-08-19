namespace Autobahn.Metrics;

/// <summary>What a metric measures, which is also what its statistics mean.</summary>
public enum MetricKind
{
    /// <summary>A running total that moves up and down. Reported as its final value.</summary>
    Counter,

    /// <summary>The current value, last write wins. Reported as min/mean/max over the interval.</summary>
    Gauge,

    /// <summary>A distribution of recorded values. Reported with percentiles.</summary>
    Histogram
}
