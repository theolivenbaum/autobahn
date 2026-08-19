namespace Autobahn.Stats;

/// <summary>The response-size distribution, in bytes.</summary>
public sealed record DataTransferStats
{
    public required long MinBytes { get; init; }
    public required long MeanBytes { get; init; }
    public required long MaxBytes { get; init; }
    public required long Percent50 { get; init; }
    public required long Percent75 { get; init; }
    public required long Percent95 { get; init; }
    public required long Percent99 { get; init; }
    public required double StdDev { get; init; }
    public required long AllBytes { get; init; }

    public static DataTransferStats Empty { get; } = new()
    {
        MinBytes = 0, MeanBytes = 0, MaxBytes = 0,
        Percent50 = 0, Percent75 = 0, Percent95 = 0, Percent99 = 0, StdDev = 0,
        AllBytes = 0
    };
}
