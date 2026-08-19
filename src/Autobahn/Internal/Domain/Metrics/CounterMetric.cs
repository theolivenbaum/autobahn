using Autobahn.Metrics;
using Autobahn.Stats;

namespace Autobahn.Internal.Domain.Metrics;

/// <summary>A running total. Writing is one interlocked add and nothing else.</summary>
internal sealed class CounterMetric(string name, MetricUnit unit)
    : MetricBase(name, MetricKind.Counter, unit), ICounter
{
    private long _total;
    private long _writes;

    private long _intervalTotal;
    private long _intervalWrites;

    public void Add(long delta)
    {
        Interlocked.Add(ref _total, delta);
        Interlocked.Add(ref _intervalTotal, delta);
        Interlocked.Increment(ref _writes);
        Interlocked.Increment(ref _intervalWrites);
    }

    public void Increment() => Add(1);
    public void Decrement() => Add(-1);

    public override MetricStats CloseInterval()
    {
        // The interval accumulators are read and cleared in one step so a write that lands
        // during the close is counted in the next interval rather than dropped.
        var total = Interlocked.Exchange(ref _intervalTotal, 0);
        var writes = Interlocked.Exchange(ref _intervalWrites, 0);

        return Snapshot(total, writes);
    }

    public override MetricStats Global() =>
        Snapshot(Interlocked.Read(ref _total), Interlocked.Read(ref _writes));

    public override void Reset()
    {
        Interlocked.Exchange(ref _total, 0);
        Interlocked.Exchange(ref _writes, 0);
        Interlocked.Exchange(ref _intervalTotal, 0);
        Interlocked.Exchange(ref _intervalWrites, 0);
    }

    private MetricStats Snapshot(long total, long writes)
    {
        var scaled = Unit.Scale(total);

        return Empty() with
        {
            Current = scaled,
            Min = scaled,
            Mean = scaled,
            Max = scaled,
            Count = writes
        };
    }
}
