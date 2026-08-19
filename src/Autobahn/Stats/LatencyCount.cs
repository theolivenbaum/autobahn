namespace Autobahn.Stats;

/// <summary>Requests bucketed into three coarse latency bands.</summary>
public sealed record LatencyCount
{
    public required int LessOrEq800 { get; init; }
    public required int More800Less1200 { get; init; }
    public required int MoreOrEq1200 { get; init; }

    public static LatencyCount Empty { get; } = new()
    {
        LessOrEq800 = 0, More800Less1200 = 0, MoreOrEq1200 = 0
    };
}
