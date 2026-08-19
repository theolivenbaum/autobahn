using Autobahn.Metrics;
using Autobahn.Stats;

namespace Autobahn.Internal.Domain.Metrics;

/// <summary>
/// The engine side of a metric: the write path user code calls, plus the two snapshots the
/// reporting pipeline takes off it.
/// </summary>
/// <remarks>
/// Every metric keeps two accumulators for the same reason the stats actor does - an
/// interval one, reset when the interval closes and used for the live view, and a global one
/// that spans the run and backs the final report. Unlike the stats actor there is no mailbox:
/// a metric write is a single interlocked operation on a field, which is cheaper than
/// publishing a message and is the only way a metric written from inside an iteration can
/// stay off the critical path.
/// </remarks>
internal abstract class MetricBase(string name, MetricKind kind, MetricUnit unit) : IMetric
{
    public string Name { get; } = name;
    public MetricKind Kind { get; } = kind;
    public MetricUnit Unit { get; } = unit;

    /// <summary>Snapshots the interval window and starts a new one.</summary>
    public abstract MetricStats CloseInterval();

    /// <summary>Snapshots the whole run so far, without disturbing anything.</summary>
    public abstract MetricStats Global();

    /// <summary>
    /// Throws both windows away without disturbing the object user code is holding. Called
    /// when the warm-up ends, so the reported series covers the bombing phase and only that.
    /// </summary>
    public abstract void Reset();

    protected MetricStats Empty() => MetricStats.Empty(Name, Kind, Unit.Name);
}
