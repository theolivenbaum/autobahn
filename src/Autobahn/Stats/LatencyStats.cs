namespace Autobahn.Stats;

/// <summary>The latency distribution, in milliseconds.</summary>
public sealed record LatencyStats
{
    public required double MinMs { get; init; }
    public required double MeanMs { get; init; }
    public required double MaxMs { get; init; }
    public required double Percent50 { get; init; }
    public required double Percent75 { get; init; }
    public required double Percent95 { get; init; }
    public required double Percent99 { get; init; }
    public required double StdDev { get; init; }
    public required LatencyCount LatencyCount { get; init; }

    public static LatencyStats Empty { get; } = new()
    {
        MinMs = 0, MeanMs = 0, MaxMs = 0,
        Percent50 = 0, Percent75 = 0, Percent95 = 0, Percent99 = 0, StdDev = 0,
        LatencyCount = LatencyCount.Empty
    };
}
