using HdrHistogram;
using Autobahn.Metrics;
using Autobahn.Stats;

namespace Autobahn.Internal.Domain.Metrics;

/// <summary>
/// A distribution, reported with percentiles. Backed by the same HdrHistogram the latency
/// numbers use, so the percentiles mean the same thing in both places.
/// </summary>
/// <remarks>
/// HdrHistogram records integers, so a value is scaled by <see cref="Precision"/> before it
/// goes in and back out again on the way to a report. That keeps two decimal places of a
/// fractional value, which is as much as anything renders.
/// </remarks>
internal sealed class HistogramMetric(string name, MetricUnit unit)
    : MetricBase(name, MetricKind.Histogram, unit), IHistogram
{
    private const long Precision = 100;

    private readonly Lock _sync = new();

    private LongHistogram _global =
        new(Constants.MaxTrackableMetricValue, Constants.MetricSignificantDigits);

    private readonly LongHistogram _interval =
        new(Constants.MaxTrackableMetricValue, Constants.MetricSignificantDigits);

    private double _current;

    public void Record(double value)
    {
        // HdrHistogram cannot hold a negative value and clamps at its ceiling rather than
        // throwing away the run, so an out-of-range recording is pinned instead of dropped.
        var raw = (long)Math.Round(value * Precision, MidpointRounding.AwayFromZero);
        var recorded = Math.Clamp(raw, 0, Constants.MaxTrackableMetricValue);

        lock (_sync)
        {
            _current = value;
            _global.RecordValue(recorded);
            _interval.RecordValue(recorded);
        }
    }

    public override MetricStats CloseInterval()
    {
        lock (_sync)
        {
            // Snapshot first, then clear in place: allocating a fresh histogram every
            // reporting interval would throw away a quarter of a megabyte each time.
            var stats = Snapshot(_interval, _current);
            _interval.Reset();
            return stats;
        }
    }

    public override MetricStats Global()
    {
        lock (_sync) return Snapshot(_global, _current);
    }

    public override void Reset()
    {
        lock (_sync)
        {
            _global.Reset();
            _interval.Reset();
        }
    }

    private MetricStats Snapshot(LongHistogram histogram, double current)
    {
        if (histogram.TotalCount == 0) return Empty();

        double Value(long raw) => Unit.Scale(raw / (double)Precision);

        return Empty() with
        {
            Current = Unit.Scale(current),
            Min = Value(MinRecorded(histogram)),
            Mean = Unit.Scale(histogram.GetMean() / Precision),
            Max = Value(histogram.GetMaxValue()),
            Count = histogram.TotalCount,
            Percent50 = Value(histogram.GetValueAtPercentile(50.0)),
            Percent75 = Value(histogram.GetValueAtPercentile(75.0)),
            Percent95 = Value(histogram.GetValueAtPercentile(95.0)),
            Percent99 = Value(histogram.GetValueAtPercentile(99.0))
        };
    }

    /// <summary>HdrHistogram exposes a max but not a min, so the lowest occupied bucket is it.</summary>
    private static long MinRecorded(LongHistogram histogram)
    {
        foreach (var bucket in histogram.RecordedValues()) return bucket.ValueIteratedTo;
        return 0;
    }
}
