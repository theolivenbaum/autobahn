using System.Runtime.CompilerServices;
using Autobahn.Internal.Domain;

namespace Autobahn;

/// <summary>Builds the response a step or scenario hands back to Autobahn.</summary>
public static class Response
{
    /// <summary>A successful response carrying nothing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Response<object> Ok() => ResponseInternal.OkEmpty();

    /// <summary>A failed response carrying nothing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Response<object> Fail() => ResponseInternal.FailEmpty<object>();

    /// <summary>A typed successful response carrying no payload.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Response<T> OkOf<T>() => new()
    {
        StatusCode = "", IsError = false, SizeBytes = 0, LatencyMs = 0, Message = ""
    };

    /// <summary>A typed failed response carrying no payload.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Response<T> FailOf<T>() => ResponseInternal.FailEmpty<T>();

    /// <param name="statusCode">Reported per status code in the stats. Empty means "not tracked".</param>
    /// <param name="sizeBytes">Counted towards data transfer. Zero means "not tracked".</param>
    /// <param name="message">Shown next to the status code in the reports.</param>
    /// <param name="latencyMs">Latency the client measured itself. Zero lets Autobahn time the call.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Response<object> Ok(
        string statusCode = "",
        long sizeBytes = 0,
        string message = "",
        double latencyMs = 0) => new()
    {
        StatusCode = statusCode,
        IsError = false,
        SizeBytes = sizeBytes,
        LatencyMs = latencyMs,
        Message = message ?? string.Empty
    };

    /// <summary>A successful response carrying a payload for the rest of the iteration to use.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Response<T> Ok<T>(
        T payload,
        string statusCode = "",
        long sizeBytes = 0,
        string message = "",
        double latencyMs = 0) => new()
    {
        StatusCode = statusCode,
        IsError = false,
        SizeBytes = sizeBytes,
        LatencyMs = latencyMs,
        Message = message ?? string.Empty,
        Payload = payload
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Response<object> Fail(
        string statusCode = "",
        string message = "",
        long sizeBytes = 0,
        double latencyMs = 0) => new()
    {
        StatusCode = statusCode,
        IsError = true,
        SizeBytes = sizeBytes,
        LatencyMs = latencyMs,
        Message = message ?? string.Empty
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Response<T> Fail<T>(
        T payload,
        string statusCode = "",
        string message = "",
        long sizeBytes = 0,
        double latencyMs = 0) => new()
    {
        StatusCode = statusCode,
        IsError = true,
        SizeBytes = sizeBytes,
        LatencyMs = latencyMs,
        Message = message ?? string.Empty,
        Payload = payload
    };
}
