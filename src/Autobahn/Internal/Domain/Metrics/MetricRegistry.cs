using System.Collections.Concurrent;
using Autobahn.Metrics;
using Autobahn.Stats;

namespace Autobahn.Internal.Domain.Metrics;

/// <summary>
/// Every metric in a run, keyed by name. Registration is idempotent, so a scenario can ask
/// for its counter from init or from the hot path without either having to be first.
/// </summary>
internal sealed class MetricRegistry : IMetricRegistry
{
    private readonly ConcurrentDictionary<string, MetricBase> _metrics = new(StringComparer.Ordinal);

    public ICounter Counter(string name, MetricUnit? unit = null) =>
        GetOrAdd<ICounter>(name, MetricKind.Counter, () => new CounterMetric(name, unit ?? MetricUnit.Count));

    public IGauge Gauge(string name, MetricUnit? unit = null) =>
        GetOrAdd<IGauge>(name, MetricKind.Gauge, () => new GaugeMetric(name, unit ?? MetricUnit.None));

    public IHistogram Histogram(string name, MetricUnit? unit = null) =>
        GetOrAdd<IHistogram>(name, MetricKind.Histogram, () => new HistogramMetric(name, unit ?? MetricUnit.None));

    /// <summary>Every metric, ordered by name so a diff between two runs is a diff of values.</summary>
    public IReadOnlyList<IMetric> All => Ordered().Cast<IMetric>().ToArray();

    public IMetric? Find(string name) => _metrics.GetValueOrDefault(name);

    /// <summary>Closes every metric's interval window and returns the snapshots, ordered by name.</summary>
    public MetricStats[] CloseInterval() => Ordered().Select(x => x.CloseInterval()).ToArray();

    /// <summary>Snapshots every metric over the whole run, ordered by name.</summary>
    public MetricStats[] Global() => Ordered().Select(x => x.Global()).ToArray();

    /// <summary>Clears every metric's accumulators, keeping the objects user code holds.</summary>
    public void Reset()
    {
        foreach (var metric in _metrics.Values) metric.Reset();
    }

    private IEnumerable<MetricBase> Ordered() =>
        _metrics.Values.OrderBy(x => x.Name, StringComparer.Ordinal);

    private T GetOrAdd<T>(string name, MetricKind kind, Func<MetricBase> create) where T : class, IMetric
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new AutobahnException("A metric needs a name.");

        var metric = _metrics.GetOrAdd(name, _ => create());

        if (metric is T typed) return typed;

        // Two kinds under one name would make the reported series mean two different things
        // depending on which write got there first, which is worse than refusing.
        throw new AutobahnException(
            $"Metric '{name}' is already registered as a {metric.Kind.ToString().ToLowerInvariant()}, "
            + $"so it cannot also be a {kind.ToString().ToLowerInvariant()}.");
    }
}
