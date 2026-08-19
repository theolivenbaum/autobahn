namespace Autobahn.Metrics;

/// <summary>
/// Where a run's metrics live. Registering the same name twice hands back the metric that is
/// already there, so a scenario can ask for its counter from init or from the hot path
/// without either having to be first.
/// </summary>
public interface IMetricRegistry
{
    /// <summary>A running total, e.g. messages published, cache misses, queue depth.</summary>
    ICounter Counter(string name, MetricUnit? unit = null);

    /// <summary>The current value of something, e.g. connection-pool size.</summary>
    IGauge Gauge(string name, MetricUnit? unit = null);

    /// <summary>A distribution, e.g. response body size, batch size, queue wait.</summary>
    IHistogram Histogram(string name, MetricUnit? unit = null);

    /// <summary>Every metric registered so far, ordered by name.</summary>
    IReadOnlyList<IMetric> All { get; }

    /// <summary>The metric registered under this name, or null.</summary>
    IMetric? Find(string name);
}
