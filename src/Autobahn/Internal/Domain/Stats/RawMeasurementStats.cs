using HdrHistogram;

namespace Autobahn.Internal.Domain.Stats;

/// <summary>The running tally for one status code within one step.</summary>
internal sealed class RawStatusCodeStats
{
    public required string StatusCode { get; init; }
    public required bool IsError { get; init; }
    public required string Message { get; init; }
    public int Count;
}

/// <summary>
/// The running tally for one side (ok or fail) of one step.
/// </summary>
/// <remarks>
/// Public mutable fields, not properties: every field here is written on the measurement
/// path, which the stats actor owns exclusively, and the histograms are already mutable
/// objects. Making them properties would add call overhead for no encapsulation gain.
/// </remarks>
internal sealed class RawItemStats
{
    public int MinMicroSec = int.MaxValue;
    public int MaxMicroSec;
    public long MinBytes = long.MaxValue;
    public long MaxBytes;
    public int RequestCount;
    public int LessOrEq800;
    public int More800Less1200;
    public int MoreOrEq1200;
    public long AllBytes;

    public readonly LongHistogram LatencyHistogram =
        new(Constants.MaxTrackableStepLatency, numberOfSignificantValueDigits: 3);

    public readonly LongHistogram DataTransferHistogram =
        new(Constants.MaxTrackableStepResponseSize, numberOfSignificantValueDigits: 3);

    public readonly Dictionary<string, RawStatusCodeStats> StatusCodes = [];
}

/// <summary>Everything accumulated for one step name, split into ok and fail.</summary>
internal sealed class RawMeasurementStats
{
    public required string Name { get; init; }
    public RawItemStats OkStats { get; } = new();
    public RawItemStats FailStats { get; } = new();

    public static RawMeasurementStats Empty(string stepName) => new() { Name = stepName };

    /// <summary>Folds one measurement into the tally. Called once per step per iteration.</summary>
    public void AddMeasurement(in Measurement measurement, long finalDataSize)
    {
        var clientRes = measurement.ClientResponse;

        // A client that timed the call itself wins; otherwise we use what the actor measured.
        var latencyMs = clientRes.LatencyMs > 0.0
            ? clientRes.LatencyMs
            : measurement.Latency.TotalMilliseconds;

        var stats = clientRes.IsError ? FailStats : OkStats;

        if (!string.IsNullOrEmpty(clientRes.StatusCode))
            UpdateStatusCodeStats(stats.StatusCodes, clientRes);

        stats.RequestCount++;

        // A non-positive latency means the response was not produced by a real round trip,
        // so it contributes to the count but not to any distribution.
        if (latencyMs <= 0.0) return;

        var latencyMicroSec = Converter.FromMsToMicroSec(latencyMs);
        stats.LatencyHistogram.RecordValue(latencyMicroSec);

        if (latencyMicroSec < stats.MinMicroSec) stats.MinMicroSec = latencyMicroSec;
        if (latencyMicroSec > stats.MaxMicroSec) stats.MaxMicroSec = latencyMicroSec;

        if (latencyMs <= 800.0) stats.LessOrEq800++;
        else if (latencyMs < 1200.0) stats.More800Less1200++;
        else stats.MoreOrEq1200++;

        if (finalDataSize <= 0) return;

        stats.AllBytes += finalDataSize;
        stats.DataTransferHistogram.RecordValue(finalDataSize);

        if (finalDataSize < stats.MinBytes) stats.MinBytes = finalDataSize;
        if (finalDataSize > stats.MaxBytes) stats.MaxBytes = finalDataSize;
    }

    private static void UpdateStatusCodeStats(Dictionary<string, RawStatusCodeStats> statuses, IResponse res)
    {
        if (statuses.TryGetValue(res.StatusCode, out var codeStats))
        {
            codeStats.Count++;
            return;
        }

        statuses[res.StatusCode] = new RawStatusCodeStats
        {
            StatusCode = res.StatusCode,
            IsError = res.IsError,
            Message = res.Message,
            Count = 1
        };
    }
}
