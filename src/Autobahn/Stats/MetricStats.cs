using Autobahn.Metrics;

namespace Autobahn.Stats;

/// <summary>
/// One metric's numbers for one window - a reporting interval, or the whole session.
/// </summary>
/// <remarks>
/// Every kind fills the same record rather than each having its own, because the reports and
/// the UI render one table over all of them. Which fields carry meaning depends on
/// <see cref="Kind"/>: a counter says everything in <see cref="Current"/>, a gauge in
/// min/mean/max, a histogram in the percentiles. All values are already scaled into
/// <see cref="Unit"/>.
/// </remarks>
public sealed record MetricStats
{
    public required string Name { get; init; }
    public required MetricKind Kind { get; init; }

    /// <summary>The display unit these values are already expressed in.</summary>
    public required string Unit { get; init; }

    /// <summary>A counter's total, a gauge's latest value, a histogram's last recording.</summary>
    public required double Current { get; init; }

    public required double Min { get; init; }
    public required double Mean { get; init; }
    public required double Max { get; init; }

    /// <summary>How many writes this window saw. A counter's write count, not its total.</summary>
    public required long Count { get; init; }

    public required double Percent50 { get; init; }
    public required double Percent75 { get; init; }
    public required double Percent95 { get; init; }
    public required double Percent99 { get; init; }

    public static MetricStats Empty(string name, MetricKind kind, string unit) => new()
    {
        Name = name,
        Kind = kind,
        Unit = unit,
        Current = 0,
        Min = 0,
        Mean = 0,
        Max = 0,
        Count = 0,
        Percent50 = 0,
        Percent75 = 0,
        Percent95 = 0,
        Percent99 = 0
    };
}
