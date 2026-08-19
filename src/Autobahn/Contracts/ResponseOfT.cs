namespace Autobahn;

/// <summary>What a step or scenario returned, with an optional payload for the caller.</summary>
/// <typeparam name="T">The payload type.</typeparam>
public sealed record Response<T> : IResponse
{
    public required string StatusCode { get; init; }
    public required bool IsError { get; init; }
    public required long SizeBytes { get; init; }
    public required double LatencyMs { get; init; }
    public required string Message { get; init; }

    /// <summary>The payload, or null when the response carries none.</summary>
    public T? Payload { get; init; }
}
