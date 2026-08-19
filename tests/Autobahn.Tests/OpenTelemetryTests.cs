using System.Diagnostics.Metrics;
using Autobahn.Internal.Domain.Metrics;
using Autobahn.Metrics;
using Autobahn.OpenTelemetry;
using Autobahn.Stats;

namespace Autobahn.Tests;

/// <summary>
/// The meter, read through a <see cref="MeterListener"/> rather than an OTLP collector: the
/// SDK's own export path is OpenTelemetry's to test, and what matters here is that Autobahn
/// publishes the right instruments with the right tags.
/// </summary>
internal class AutobahnMeterTests
{
    private static TimeLineHistoryRecord Record() => new()
    {
        Duration = TimeSpan.FromSeconds(5),
        ScenarioStats =
        [
            new ScenarioStats
            {
                ScenarioName = "checkout",
                Ok = MeasurementStats.Empty with
                {
                    Request = new RequestStats { Count = 300, RPS = 60 },
                    Latency = LatencyStats.Empty with { MeanMs = 21, Percent50 = 20, Percent95 = 40, Percent99 = 55, MaxMs = 90 },
                    DataTransfer = DataTransferStats.Empty with { AllBytes = 4_096 },
                    StatusCodes = [new StatusCodeStats { StatusCode = "200", IsError = false, Message = "", Count = 300 }]
                },
                Fail = MeasurementStats.Empty with
                {
                    Request = new RequestStats { Count = 5, RPS = 1 },
                    StatusCodes = [new StatusCodeStats { StatusCode = "500", IsError = true, Message = "boom", Count = 5 }]
                },
                StepStats =
                [
                    new StepStats
                    {
                        StepName = "pay",
                        Ok = MeasurementStats.Empty with { Request = new RequestStats { Count = 300, RPS = 60 } },
                        Fail = MeasurementStats.Empty
                    }
                ],
                LoadSimulationStats = new LoadSimulationStats { SimulationName = "inject", Value = 60 },
                CurrentOperation = OperationType.Bombing,
                AllRequestCount = 305,
                AllOkCount = 300,
                AllFailCount = 5,
                AllBytes = 4_096,
                Duration = TimeSpan.FromSeconds(5)
            }
        ],
        Metrics = [MetricStats.Empty("cache.miss", MetricKind.Counter, "count") with { Current = 42 }]
    };

    /// <summary>Collects one round of every instrument the meter publishes.</summary>
    private static List<(string Instrument, double Value, Dictionary<string, object?> Tags)> Collect(
        Action<AutobahnMeter> publish)
    {
        var readings = new List<(string, double, Dictionary<string, object?>)>();

        // Every AutobahnMeter has the same name, so a listener that filtered on the name alone
        // would also pick up whatever a sibling test is publishing. The version is this
        // instance's own, and is what tells them apart.
        var id = Guid.NewGuid().ToString("N");

        using var meter = new AutobahnMeter(
            new TestInfo { SessionId = "s1", TestSuite = "suite", TestName = "test" }, version: id);

        using var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AutobahnMeter.MeterName && instrument.Meter.Version == id)
                l.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            readings.Add((instrument.Name, value, tags.ToArray().ToDictionary(x => x.Key, x => x.Value))));

        listener.Start();

        publish(meter);
        listener.RecordObservableInstruments();

        return readings;
    }

    [Test]
    public async Task Nothing_is_published_before_the_first_interval()
    {
        var readings = Collect(_ => { });

        await Assert.That(readings).IsEmpty();
    }

    [Test]
    public async Task A_scenarios_numbers_are_published_with_the_run_as_their_tags()
    {
        var readings = Collect(meter => meter.Publish(Record()));

        var ok = readings.Single(x => x.Instrument == "autobahn.requests.ok" && !x.Tags.ContainsKey("step"));

        await Assert.That(ok.Value).IsEqualTo(300);
        await Assert.That(ok.Tags["scenario"]).IsEqualTo("checkout");
        await Assert.That(ok.Tags["session_id"]).IsEqualTo("s1");
        await Assert.That(ok.Tags["test_suite"]).IsEqualTo("suite");
        await Assert.That(ok.Tags["test_name"]).IsEqualTo("test");
    }

    [Test]
    public async Task A_step_is_the_same_instrument_told_apart_by_a_tag()
    {
        var readings = Collect(meter => meter.Publish(Record()));

        var step = readings.Single(x => x.Instrument == "autobahn.requests.ok" && x.Tags.ContainsKey("step"));

        await Assert.That(step.Tags["step"]).IsEqualTo("pay");
        await Assert.That(step.Tags["scenario"]).IsEqualTo("checkout");
        await Assert.That(step.Value).IsEqualTo(300);
    }

    [Test]
    public async Task Latency_throughput_and_data_all_publish()
    {
        var readings = Collect(meter => meter.Publish(Record()));

        double Scenario(string instrument) =>
            readings.Single(x => x.Instrument == instrument && !x.Tags.ContainsKey("step")).Value;

        await Assert.That(Scenario("autobahn.requests.fail")).IsEqualTo(5);
        await Assert.That(Scenario("autobahn.requests.rps")).IsEqualTo(60);
        await Assert.That(Scenario("autobahn.latency.mean")).IsEqualTo(21);
        await Assert.That(Scenario("autobahn.latency.p50")).IsEqualTo(20);
        await Assert.That(Scenario("autobahn.latency.p95")).IsEqualTo(40);
        await Assert.That(Scenario("autobahn.latency.p99")).IsEqualTo(55);
        await Assert.That(Scenario("autobahn.latency.max")).IsEqualTo(90);
        await Assert.That(Scenario("autobahn.data.bytes")).IsEqualTo(4_096);
    }

    [Test]
    public async Task Status_codes_publish_one_series_each()
    {
        var readings = Collect(meter => meter.Publish(Record()))
            .Where(x => x.Instrument == "autobahn.status_code")
            .ToArray();

        var ok = readings.Single(x => (string?)x.Tags["status_code"] == "200");
        var error = readings.Single(x => (string?)x.Tags["status_code"] == "500");

        await Assert.That(ok.Value).IsEqualTo(300);
        await Assert.That((bool?)ok.Tags["is_error"]).IsFalse();
        await Assert.That(error.Value).IsEqualTo(5);
        await Assert.That((bool?)error.Tags["is_error"]).IsTrue();
    }

    [Test]
    public async Task The_runs_own_metrics_publish_under_one_instrument_named_by_a_tag()
    {
        var reading = Collect(meter => meter.Publish(Record()))
            .Single(x => x.Instrument == "autobahn.metric");

        await Assert.That(reading.Value).IsEqualTo(42);
        await Assert.That(reading.Tags["metric"]).IsEqualTo("cache.miss");
        await Assert.That(reading.Tags["kind"]).IsEqualTo("counter");
    }

    [Test]
    public async Task The_latest_interval_replaces_the_one_before_it()
    {
        var second = Record() with
        {
            ScenarioStats =
            [
                Record().ScenarioStats[0] with
                {
                    Ok = MeasurementStats.Empty with { Request = new RequestStats { Count = 999, RPS = 200 } },
                    StepStats = []
                }
            ]
        };

        var readings = Collect(meter =>
        {
            meter.Publish(Record());
            meter.Publish(second);
        });

        await Assert.That(readings.Single(x => x.Instrument == "autobahn.requests.ok").Value).IsEqualTo(999);
    }
}

[NotInParallel]
public class IntervalObserverTests
{
    [Test]
    [Category("slow")]
    public async Task The_observer_sees_every_closed_interval()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<TimeLineHistoryRecord>();

        AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("observed", async ctx =>
                    {
                        ctx.Metrics.Counter("ticks").Increment();
                        await Task.Delay(Time.Milliseconds(10), ctx.CancellationToken);
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.Inject(rate: 20, interval: Time.Seconds(1), during: Time.Seconds(12))))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .WithReportingInterval(Time.Seconds(5))
            .WithIntervalObserver(record =>
            {
                seen.Add(record);
                return Task.CompletedTask;
            })
            .Run();

        // Two full intervals in twelve seconds; the partial third is not emitted as a full one.
        await Assert.That(seen.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(seen.All(x => x.ScenarioStats.Length == 1)).IsTrue();
        await Assert.That(seen.All(x => x.Metrics.Any(m => m.Name == "ticks"))).IsTrue();
        await Assert.That(seen.Select(x => x.Duration).Distinct().Count()).IsEqualTo(seen.Count);
    }

    [Test]
    [Category("slow")]
    public async Task An_observer_that_throws_does_not_take_the_run_with_it()
    {
        var calls = 0;

        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("observed", async ctx =>
                    {
                        await Task.Delay(Time.Milliseconds(10), ctx.CancellationToken);
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.Inject(rate: 20, interval: Time.Seconds(1), during: Time.Seconds(11))))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .WithReportingInterval(Time.Seconds(5))
            .WithIntervalObserver(_ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("the collector is down");
            })
            .Run();

        // An export that broke is not a reason to lose the test.
        await Assert.That(calls).IsGreaterThan(0);
        await Assert.That(stats.AllOkCount).IsGreaterThan(100);
    }
}
