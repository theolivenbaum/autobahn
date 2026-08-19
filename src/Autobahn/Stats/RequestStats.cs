namespace Autobahn.Stats;

/// <summary>How many requests happened, and how fast they arrived.</summary>
public sealed record RequestStats
{
    public required int Count { get; init; }
    public required double RPS { get; init; }

    public static RequestStats Empty { get; } = new() { Count = 0, RPS = 0 };
}
