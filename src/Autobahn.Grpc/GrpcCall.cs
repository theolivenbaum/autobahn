using System.Diagnostics;
using Grpc.Core;

namespace Autobahn.Grpc;

/// <summary>
/// Measures a call made through a generated gRPC client.
/// </summary>
/// <remarks>
/// Deliberately thin. The generated client already is the API - typed, discoverable, and
/// exactly what the service's <c>.proto</c> says - so wrapping it would mean reproducing that
/// surface badly. What Autobahn adds is the measurement: the status code gRPC actually
/// returned, the size of what crossed the wire, and the iteration's cancellation reaching the
/// call rather than being ignored.
/// </remarks>
public static class GrpcCall
{
    /// <summary>Measures a unary call.</summary>
    /// <example>
    /// <code>
    /// await GrpcCall.Unary("GetUser", context, ct => client.GetUserAsync(request, cancellationToken: ct));
    /// </code>
    /// </example>
    public static Task<Response<TResponse>> Unary<TResponse>(
        string name,
        IScenarioContext context,
        Func<CancellationToken, AsyncUnaryCall<TResponse>> call,
        TimeSpan? timeout = null,
        Func<TResponse, long>? sizeOf = null) =>
        Measure(name, context, timeout, async token =>
        {
            using var unary = call(token);

            var response = await unary.ResponseAsync.ConfigureAwait(false);
            var trailers = unary.GetTrailers();

            return (response, unary.GetStatus(), trailers, sizeOf?.Invoke(response) ?? 0);
        });

    /// <summary>
    /// Measures a server-streaming call, all the way to the end of the stream.
    /// </summary>
    /// <remarks>
    /// The latency of a stream is the whole stream, which is the only figure that means
    /// anything for one: time to first message says nothing about the other thousand, and
    /// per-message latency is a different measurement that belongs in a metric.
    /// </remarks>
    public static Task<Response<int>> ServerStreaming<TResponse>(
        string name,
        IScenarioContext context,
        Func<CancellationToken, AsyncServerStreamingCall<TResponse>> call,
        TimeSpan? timeout = null,
        Action<TResponse>? onMessage = null,
        Func<TResponse, long>? sizeOf = null) =>
        Measure(name, context, timeout, async token =>
        {
            using var streaming = call(token);

            var count = 0;
            var bytes = 0L;

            await foreach (var message in streaming.ResponseStream.ReadAllAsync(token).ConfigureAwait(false))
            {
                count++;
                bytes += sizeOf?.Invoke(message) ?? 0;
                onMessage?.Invoke(message);
            }

            return (count, streaming.GetStatus(), streaming.GetTrailers(), bytes);
        });

    /// <summary>
    /// Measures a call the caller drives itself: client-streaming, duplex, or anything else
    /// that does not fit the two shapes above.
    /// </summary>
    public static Task<Response<TResult>> Custom<TResult>(
        string name,
        IScenarioContext context,
        Func<CancellationToken, Task<(TResult Result, Status Status, long SizeBytes)>> call,
        TimeSpan? timeout = null) =>
        Measure(name, context, timeout, async token =>
        {
            var (result, status, size) = await call(token).ConfigureAwait(false);
            return (result, status, Metadata.Empty, size);
        });

    private static async Task<Response<T>> Measure<T>(
        string name,
        IScenarioContext context,
        TimeSpan? timeout,
        Func<CancellationToken, Task<(T Value, Status Status, Metadata Trailers, long SizeBytes)>> call)
    {
        using var timeoutCts = timeout is { } limit
            ? CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken)
            : null;

        timeoutCts?.CancelAfter(timeout!.Value);
        var token = timeoutCts?.Token ?? context.CancellationToken;

        var started = Stopwatch.GetTimestamp();

        try
        {
            var (value, status, _, sizeBytes) = await call(token).ConfigureAwait(false);
            var latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            return status.StatusCode == StatusCode.OK
                ? Response.Ok(value, statusCode: nameof(StatusCode.OK), sizeBytes: sizeBytes, latencyMs: latency)
                : Response.Fail(
                    value,
                    statusCode: status.StatusCode.ToString(),
                    sizeBytes: sizeBytes,
                    message: status.Detail,
                    latencyMs: latency);
        }
        catch (RpcException ex)
        {
            // gRPC's own failure, with the code the server or the channel produced. Reported
            // by name rather than by number: "Unavailable" is what a person reads in a report.
            return Response.FailOf<T>() with
            {
                StatusCode = ex.StatusCode.ToString(),
                Message = ex.Status.Detail,
                LatencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            };
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true
                                                 && !context.CancellationToken.IsCancellationRequested)
        {
            return Response.FailOf<T>() with
            {
                StatusCode = nameof(StatusCode.DeadlineExceeded),
                Message = $"call '{name}' timed out after {timeout}",
                LatencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            };
        }
    }
}
