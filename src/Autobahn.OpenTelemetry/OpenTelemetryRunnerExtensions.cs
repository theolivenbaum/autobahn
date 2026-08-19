using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Autobahn.Stats;

namespace Autobahn.OpenTelemetry;

/// <summary>
/// Pushes a run's statistics and metrics to an OpenTelemetry collector.
/// </summary>
/// <remarks>
/// The one export worth building into Autobahn, because it reaches every backend the user
/// already runs rather than adding another. Two halves: each reporting interval goes out
/// while the run happens, and the last one is flushed before the process can exit.
///
/// This is not a reporting sink and does not bring the concept back. It is one call that
/// registers an interval callback with the runner; the run knows nothing about it, and it
/// knows nothing about the run beyond the records it is handed.
/// </remarks>
public static class OpenTelemetryRunnerExtensions
{
    /// <summary>
    /// Exports every reporting interval over OTLP, and flushes the last one when the run ends.
    /// </summary>
    /// <remarks>
    /// The provider is disposed when the returned <see cref="SessionResult"/>'s run finishes,
    /// which is why this hands back a disposable: keep it in a <c>using</c> around the
    /// <c>Run()</c> call, or the last interval may never leave the process.
    /// </remarks>
    public static AutobahnContext WithOpenTelemetry(
        this AutobahnContext context, out IDisposable exporter, AutobahnOtlpOptions? options = null)
    {
        var settings = options ?? new AutobahnOtlpOptions();
        var session = new OtlpSession(settings, context);

        exporter = session;

        return context.WithIntervalObserver(record =>
        {
            session.Publish(record);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The meter, the provider and the last flush, kept together so a caller has one thing to
    /// dispose.
    /// </summary>
    private sealed class OtlpSession : IDisposable
    {
        private readonly AutobahnMeter _meter;
        private readonly MeterProvider _provider;

        public OtlpSession(AutobahnOtlpOptions options, AutobahnContext context)
        {
            // The session id is not known until the run starts, so what is exported carries
            // the suite and test name the context already has; the run's own id arrives with
            // the first interval and is not something a metric tag should churn on anyway.
            _meter = new AutobahnMeter(
                new TestInfo
                {
                    SessionId = "",
                    TestSuite = context.TestSuite,
                    TestName = context.TestName
                },
                options.ServiceVersion);

            var resource = ResourceBuilder
                .CreateDefault()
                .AddService(options.ServiceName, serviceVersion: options.ServiceVersion);

            if (options.ResourceAttributes is { Count: > 0 } attributes)
                resource.AddAttributes(attributes);

            _provider = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resource)
                .AddMeter(AutobahnMeter.MeterName)
                .AddOtlpExporter((exporterOptions, readerOptions) =>
                {
                    if (options.Endpoint is { } endpoint) exporterOptions.Endpoint = new Uri(endpoint);
                    if (options.Headers is { } headers) exporterOptions.Headers = headers;

                    exporterOptions.Protocol = options.Protocol;

                    if (options.ExportInterval is { } interval)
                    {
                        readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                            (int)interval.TotalMilliseconds;
                    }
                })
                .Build()!;
        }

        public void Publish(TimeLineHistoryRecord record) => _meter.Publish(record);

        public void Dispose()
        {
            // The last interval is published moments before the run returns, so without a
            // forced flush the process can exit with it still buffered - which is exactly the
            // interval somebody looking at the dashboard afterwards wants.
            _provider.ForceFlush(timeoutMilliseconds: 10_000);
            _provider.Dispose();
            _meter.Dispose();
        }
    }
}
