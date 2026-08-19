using Autobahn.Metrics;
using Autobahn.Stats;

namespace Autobahn.Internal.Domain.Metrics;

/// <summary>
/// The current value of something. The last write wins, and min/mean/max describe how it
/// moved over the window.
/// </summary>
internal sealed class GaugeMetric(string name, MetricUnit unit)
    : MetricBase(name, MetricKind.Gauge, unit), IGauge
{
    private readonly Lock _sync = new();

    private double _current;

    private double _globalMin = double.MaxValue;
    private double _globalMax = double.MinValue;
    private double _globalSum;
    private long _globalCount;

    private double _intervalMin = double.MaxValue;
    private double _intervalMax = double.MinValue;
    private double _intervalSum;
    private long _intervalCount;

    public void Set(double value)
    {
        // A gauge's aggregates cannot be maintained with a single interlocked op the way a
        // counter's can, so this takes the cheapest lock there is. A gauge is written on the
        // order of once per sample, not once per request; anything on the hot path wants a
        // counter or a histogram instead.
        lock (_sync)
        {
            _current = value;

            if (value < _globalMin) _globalMin = value;
            if (value > _globalMax) _globalMax = value;
            _globalSum += value;
            _globalCount++;

            if (value < _intervalMin) _intervalMin = value;
            if (value > _intervalMax) _intervalMax = value;
            _intervalSum += value;
            _intervalCount++;
        }
    }

    public override MetricStats CloseInterval()
    {
        lock (_sync)
        {
            var stats = Snapshot(_current, _intervalMin, _intervalMax, _intervalSum, _intervalCount);

            _intervalMin = double.MaxValue;
            _intervalMax = double.MinValue;
            _intervalSum = 0;
            _intervalCount = 0;

            return stats;
        }
    }

    public override MetricStats Global()
    {
        lock (_sync) return Snapshot(_current, _globalMin, _globalMax, _globalSum, _globalCount);
    }

    public override void Reset()
    {
        lock (_sync)
        {
            _globalMin = _intervalMin = double.MaxValue;
            _globalMax = _intervalMax = double.MinValue;
            _globalSum = _intervalSum = 0;
            _globalCount = _intervalCount = 0;
        }
    }

    private MetricStats Snapshot(double current, double min, double max, double sum, long count)
    {
        if (count == 0) return Empty();

        var mean = sum / count;

        return Empty() with
        {
            Current = Unit.Scale(current),
            Min = Unit.Scale(min),
            Mean = Unit.Scale(mean),
            Max = Unit.Scale(max),
            Count = count,
            // A gauge has no distribution of its own; repeating the mean would read as one.
            Percent50 = Unit.Scale(mean),
            Percent75 = Unit.Scale(mean),
            Percent95 = Unit.Scale(max),
            Percent99 = Unit.Scale(max)
        };
    }
}
