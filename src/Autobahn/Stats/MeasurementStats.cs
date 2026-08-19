namespace Autobahn.Stats;

/// <summary>Everything measured about one side (ok or fail) of a step or scenario.</summary>
public sealed record MeasurementStats
{
    public required RequestStats Request { get; init; }
    public required LatencyStats Latency { get; init; }
    public required DataTransferStats DataTransfer { get; init; }
    public required StatusCodeStats[] StatusCodes { get; init; }

    public static MeasurementStats Empty { get; } = new()
    {
        Request = RequestStats.Empty,
        Latency = LatencyStats.Empty,
        DataTransfer = DataTransferStats.Empty,
        StatusCodes = []
    };
}
