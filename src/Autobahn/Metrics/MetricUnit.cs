namespace Autobahn.Metrics;

/// <summary>
/// How a metric's raw value is displayed: what to call the unit, and what to multiply the
/// raw value by to get there.
/// </summary>
/// <remarks>
/// Writers record raw values - bytes, not megabytes - because that is what the code that
/// produces them has to hand, and because scaling on the write path would be arithmetic on
/// every write for the sake of the one place that formats. The scale is applied once, when
/// the interval is closed.
/// </remarks>
public sealed record MetricUnit
{
    /// <summary>What the scaled value is called, e.g. "MB". Empty for a bare number.</summary>
    public required string Name { get; init; }

    /// <summary>What a raw value is multiplied by to reach <see cref="Name"/>.</summary>
    public required double ScalingFactor { get; init; }

    /// <summary>How many decimal places the scaled value is worth showing.</summary>
    public int Decimals { get; init; } = Constants.StatsRounding;

    public static MetricUnit Create(string name, double scalingFactor = 1.0, int decimals = Constants.StatsRounding) =>
        new() { Name = name, ScalingFactor = scalingFactor, Decimals = decimals };

    /// <summary>A bare number with no unit. Keeps two decimals, since it could be anything.</summary>
    public static MetricUnit None { get; } = new() { Name = "", ScalingFactor = 1.0 };

    public static MetricUnit Count { get; } = new() { Name = "count", ScalingFactor = 1.0, Decimals = 0 };
    public static MetricUnit Bytes { get; } = new() { Name = "bytes", ScalingFactor = 1.0, Decimals = 0 };
    public static MetricUnit Kilobytes { get; } = new() { Name = "KB", ScalingFactor = 1.0 / 1_024 };
    public static MetricUnit Megabytes { get; } = new() { Name = "MB", ScalingFactor = 1.0 / (1_024 * 1_024) };
    public static MetricUnit Milliseconds { get; } = new() { Name = "ms", ScalingFactor = 1.0 };
    public static MetricUnit Seconds { get; } = new() { Name = "sec", ScalingFactor = 1.0 / 1_000 };
    public static MetricUnit Percent { get; } = new() { Name = "%", ScalingFactor = 1.0, Decimals = 1 };

    /// <summary>Applies the scale, rounded to the unit's own precision.</summary>
    public double Scale(double rawValue) => Math.Round(rawValue * ScalingFactor, Decimals);
}
