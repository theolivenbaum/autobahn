using System.Diagnostics.Metrics;
using Autobahn.Stats;

namespace Autobahn.OpenTelemetry;

/// <summary>
/// Publishes a run's latest interval through <see cref="Meter"/> instruments, so the
/// OpenTelemetry SDK can export them like anything else.
/// </summary>
/// <remarks>
/// Observable instruments rather than counters that are written to: the stats already exist
/// as a snapshot per interval, and re-deriving deltas from them so a counter could be
/// incremented would be arithmetic in service of the wrong shape. An observable gauge asks
/// "what is it now", which is exactly what a snapshot answers.
///
/// The tag set is deliberately the identity of the thing measured - session, suite, test,
/// scenario, step - and nothing else. A tag whose value changes every interval would make
/// each interval its own time series, which is the standard way to make a metrics backend
/// unusable.
/// </remarks>
internal sealed class AutobahnMeter : IDisposable
{
    /// <summary>The meter name a collector filters on.</summary>
    public const string MeterName = "Autobahn";

    private readonly Meter _meter;
    private readonly TestInfo _testInfo;

    private volatile TimeLineHistoryRecord? _latest;

    public AutobahnMeter(TestInfo testInfo, string? version)
    {
        _testInfo = testInfo;
        _meter = new Meter(MeterName, version);

        Observe("autobahn.requests.ok", "{request}", "Successful requests in the interval", x => x.Ok.Request.Count);
        Observe("autobahn.requests.fail", "{request}", "Failed requests in the interval", x => x.Fail.Request.Count);
        Observe("autobahn.requests.rps", "{request}/s", "Successful requests per second", x => x.Ok.Request.RPS);

        Observe("autobahn.latency.mean", "ms", "Mean latency of successful requests", x => x.Ok.Latency.MeanMs);
        Observe("autobahn.latency.p50", "ms", "50th percentile latency", x => x.Ok.Latency.Percent50);
        Observe("autobahn.latency.p95", "ms", "95th percentile latency", x => x.Ok.Latency.Percent95);
        Observe("autobahn.latency.p99", "ms", "99th percentile latency", x => x.Ok.Latency.Percent99);
        Observe("autobahn.latency.max", "ms", "Highest latency seen", x => x.Ok.Latency.MaxMs);

        Observe("autobahn.data.bytes", "By", "Bytes transferred by successful requests", x => x.Ok.DataTransfer.AllBytes);

        _meter.CreateObservableGauge("autobahn.metric", ObserveMetrics, description: "A metric the run collected");
        _meter.CreateObservableGauge("autobahn.status_code", ObserveStatusCodes, "{response}", "Responses per status code");
    }

    /// <summary>Publishes an interval. The next export reads whatever was last published.</summary>
    public void Publish(TimeLineHistoryRecord record) => _latest = record;

    /// <summary>
    /// One instrument over both the scenario's totals and each of its steps, told apart by the
    /// <c>step</c> tag - because they are the same measurement at two granularities, and
    /// splitting them into two instruments would make every query ask for both.
    /// </summary>
    private void Observe(string name, string unit, string description, Func<ScenarioStats, double> scenarioValue)
    {
        _meter.CreateObservableGauge(name, () => Collect(scenarioValue), unit, description);
    }

    private IEnumerable<Measurement<double>> Collect(Func<ScenarioStats, double> read)
    {
        if (_latest is not { } record) yield break;

        foreach (var scenario in record.ScenarioStats)
        {
            yield return new Measurement<double>(read(scenario), Tags(scenario.ScenarioName));

            foreach (var step in scenario.StepStats)
            {
                // A step's stats have the same shape as a scenario's, so the same reader works
                // over a scenario-shaped view of one.
                yield return new Measurement<double>(
                    read(AsScenario(scenario, step)),
                    Tags(scenario.ScenarioName, step.StepName));
            }
        }
    }

    private IEnumerable<Measurement<double>> ObserveMetrics()
    {
        if (_latest is not { } record) yield break;

        foreach (var metric in record.Metrics)
        {
            yield return new Measurement<double>(metric.Current,
            [
                .. BaseTags(),
                new KeyValuePair<string, object?>("metric", metric.Name),
                new KeyValuePair<string, object?>("kind", metric.Kind.ToString().ToLowerInvariant()),
                new KeyValuePair<string, object?>("unit", metric.Unit)
            ]);
        }
    }

    private IEnumerable<Measurement<double>> ObserveStatusCodes()
    {
        if (_latest is not { } record) yield break;

        foreach (var scenario in record.ScenarioStats)
        {
            foreach (var code in scenario.Ok.StatusCodes.Concat(scenario.Fail.StatusCodes))
            {
                yield return new Measurement<double>(code.Count,
                [
                    .. BaseTags(),
                    new KeyValuePair<string, object?>("scenario", scenario.ScenarioName),
                    new KeyValuePair<string, object?>("status_code", code.StatusCode),
                    new KeyValuePair<string, object?>("is_error", code.IsError)
                ]);
            }
        }
    }

    private static ScenarioStats AsScenario(ScenarioStats scenario, StepStats step) =>
        scenario with { Ok = step.Ok, Fail = step.Fail, StepStats = [] };

    private KeyValuePair<string, object?>[] Tags(string scenarioName, string? stepName = null) =>
        stepName is null
            ? [.. BaseTags(), new KeyValuePair<string, object?>("scenario", scenarioName)]
            :
            [
                .. BaseTags(),
                new KeyValuePair<string, object?>("scenario", scenarioName),
                new KeyValuePair<string, object?>("step", stepName)
            ];

    private KeyValuePair<string, object?>[] BaseTags() =>
    [
        new("session_id", _testInfo.SessionId),
        new("test_suite", _testInfo.TestSuite),
        new("test_name", _testInfo.TestName)
    ];

    public void Dispose() => _meter.Dispose();
}
