namespace Autobahn.Stats;

/// <summary>The ok and fail measurements for one named step.</summary>
public sealed record StepStats
{
    public required string StepName { get; init; }
    public required MeasurementStats Ok { get; init; }
    public required MeasurementStats Fail { get; init; }
}
