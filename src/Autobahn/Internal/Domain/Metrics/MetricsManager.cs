using Autobahn.Stats;
using Microsoft.Extensions.Logging;

namespace Autobahn.Internal.Domain.Metrics;

/// <summary>
/// One run's metrics: the registry user code writes to, and the runtime collector that fills
/// the built-in series.
/// </summary>
/// <remarks>
/// Kept separate from the stats actor on purpose. The two answer different questions and
/// have different write patterns - a measurement is published once per iteration through a
/// mailbox, while a metric is written whenever anything feels like it - and folding metrics
/// into the actor would put every metric write behind the same channel as the measurements.
/// They meet again at the reporting interval, which is the only place both are read.
/// </remarks>
internal sealed class MetricsManager : IDisposable
{
    private readonly RuntimeMetrics? _runtime;

    public MetricsManager(ILogger logger, bool collectRuntimeMetrics)
    {
        Registry = new MetricRegistry();

        if (collectRuntimeMetrics) _runtime = new RuntimeMetrics(Registry, logger);
    }

    public MetricRegistry Registry { get; }

    public void Start() => _runtime?.Start(Constants.MetricsSampleInterval);

    /// <summary>
    /// Closes the interval window on every metric. Takes one last runtime sample first, so
    /// the interval that has just ended is described by a sample taken inside it rather than
    /// by whatever the timer last happened to catch.
    /// </summary>
    public MetricStats[] CloseInterval()
    {
        _runtime?.Sample();
        return Registry.CloseInterval();
    }

    /// <summary>
    /// Throws away everything collected so far. Called when the warm-up ends, so the reported
    /// series describe the bombing phase and only that - the same window every other number
    /// in the report covers.
    /// </summary>
    public void Reset() => Registry.Reset();

    /// <summary>Snapshots every metric over the whole run.</summary>
    public MetricStats[] Global()
    {
        _runtime?.Sample();
        return Registry.Global();
    }

    public void Dispose() => _runtime?.Dispose();
}
