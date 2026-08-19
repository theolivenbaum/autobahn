using OpenTelemetry.Exporter;

namespace Autobahn.OpenTelemetry;

/// <summary>Where a run's numbers are pushed, and what they are labelled with.</summary>
public sealed record AutobahnOtlpOptions
{
    /// <summary>
    /// The collector's OTLP endpoint. Null uses the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>
    /// environment variable, which is how a collector is usually configured already.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>gRPC by default, which is what a collector listens for on 4317.</summary>
    public OtlpExportProtocol Protocol { get; init; } = OtlpExportProtocol.Grpc;

    /// <summary>Headers sent with each export, e.g. an API key. Format: <c>key=value,key=value</c>.</summary>
    public string? Headers { get; init; }

    /// <summary>What the run calls itself in the collector. Defaults to "autobahn".</summary>
    public string ServiceName { get; init; } = "autobahn";

    public string? ServiceVersion { get; init; }

    /// <summary>
    /// How often the exporter pushes. Defaults to the run's own reporting interval, which is
    /// the rate the numbers actually change at - exporting faster only repeats them.
    /// </summary>
    public TimeSpan? ExportInterval { get; init; }

    /// <summary>Extra resource attributes attached to everything this run exports.</summary>
    public IReadOnlyDictionary<string, object>? ResourceAttributes { get; init; }
}
