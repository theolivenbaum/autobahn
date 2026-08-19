namespace Autobahn.Stats;

/// <summary>How often one status code came back, and what it meant.</summary>
public sealed record StatusCodeStats
{
    public required string StatusCode { get; init; }
    public required bool IsError { get; init; }
    public required string Message { get; init; }
    public required int Count { get; init; }
}
