namespace Autobahn.Metrics;

/// <summary>A named numeric series collected over a run.</summary>
public interface IMetric
{
    string Name { get; }
    MetricKind Kind { get; }
    MetricUnit Unit { get; }
}

/// <summary>A running total. Writing to one is a single interlocked add.</summary>
public interface ICounter : IMetric
{
    void Add(long delta);
    void Increment();
    void Decrement();
}

/// <summary>The current value of something. Last write wins.</summary>
public interface IGauge : IMetric
{
    void Set(double value);
}

/// <summary>A distribution of values, reported with percentiles.</summary>
public interface IHistogram : IMetric
{
    void Record(double value);
}
