using Autobahn.Internal.Domain.Metrics;
using Autobahn.Metrics;
using Autobahn.Stats;

namespace Autobahn.Tests;

internal class MetricRegistryTests
{
    [Test]
    public async Task Registering_the_same_name_twice_hands_back_the_same_metric()
    {
        var registry = new MetricRegistry();

        var first = registry.Counter("orders");
        var second = registry.Counter("orders");

        await Assert.That(second).IsSameReferenceAs(first);
        await Assert.That(registry.All.Count).IsEqualTo(1);
    }

    [Test]
    public async Task One_name_cannot_be_two_kinds()
    {
        var registry = new MetricRegistry();
        registry.Counter("orders");

        var ex = Assert.Throws<AutobahnException>(() => registry.Gauge("orders"));

        await Assert.That(ex!.Message).Contains("orders");
        await Assert.That(ex.Message).Contains("counter");
        await Assert.That(ex.Message).Contains("gauge");
    }

    [Test]
    public async Task A_metric_needs_a_name()
    {
        var registry = new MetricRegistry();

        await Assert.That(Assert.Throws<AutobahnException>(() => registry.Counter("  "))).IsNotNull();
    }

    [Test]
    public async Task Metrics_come_back_ordered_by_name_whatever_order_they_were_registered_in()
    {
        var registry = new MetricRegistry();

        registry.Counter("zulu");
        registry.Gauge("alpha");
        registry.Histogram("mike");
        registry.Counter("bravo");

        await Assert.That(registry.All.Select(x => x.Name)).IsEquivalentTo(new[] { "alpha", "bravo", "mike", "zulu" });
        await Assert.That(registry.Global().Select(x => x.Name)).IsEquivalentTo(new[] { "alpha", "bravo", "mike", "zulu" });
    }
}

internal class CounterMetricTests
{
    [Test]
    public async Task A_counter_reports_its_running_total_and_moves_both_ways()
    {
        var registry = new MetricRegistry();
        var counter = registry.Counter("depth");

        counter.Add(10);
        counter.Increment();
        counter.Decrement();
        counter.Decrement();

        var stats = registry.Global().Single();

        await Assert.That(stats.Kind).IsEqualTo(MetricKind.Counter);
        await Assert.That(stats.Current).IsEqualTo(9);
        await Assert.That(stats.Count).IsEqualTo(4);
    }

    [Test]
    public async Task Closing_an_interval_starts_the_interval_total_again_but_leaves_the_global_one()
    {
        var registry = new MetricRegistry();
        var counter = registry.Counter("published");

        counter.Add(5);
        var first = registry.CloseInterval().Single();

        counter.Add(3);
        var second = registry.CloseInterval().Single();

        await Assert.That(first.Current).IsEqualTo(5);
        await Assert.That(second.Current).IsEqualTo(3);
        await Assert.That(registry.Global().Single().Current).IsEqualTo(8);
    }

    [Test]
    public async Task A_counters_unit_scales_what_it_reports_but_not_what_it_stores()
    {
        var registry = new MetricRegistry();
        var counter = registry.Counter("sent", MetricUnit.Kilobytes);

        counter.Add(2_048);

        var stats = registry.Global().Single();

        await Assert.That(stats.Unit).IsEqualTo("KB");
        await Assert.That(stats.Current).IsEqualTo(2);
    }

    [Test]
    public async Task Concurrent_writers_all_land()
    {
        var registry = new MetricRegistry();
        var counter = registry.Counter("hits");

        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 10_000; i++) counter.Increment();
        });

        await Assert.That(registry.Global().Single().Current).IsEqualTo(80_000);
    }
}

internal class GaugeMetricTests
{
    [Test]
    public async Task A_gauge_reports_its_latest_value_and_how_it_moved()
    {
        var registry = new MetricRegistry();
        var gauge = registry.Gauge("pool");

        gauge.Set(10);
        gauge.Set(30);
        gauge.Set(20);

        var stats = registry.Global().Single();

        await Assert.That(stats.Kind).IsEqualTo(MetricKind.Gauge);
        await Assert.That(stats.Current).IsEqualTo(20);
        await Assert.That(stats.Min).IsEqualTo(10);
        await Assert.That(stats.Max).IsEqualTo(30);
        await Assert.That(stats.Mean).IsEqualTo(20);
        await Assert.That(stats.Count).IsEqualTo(3);
    }

    [Test]
    public async Task A_gauge_that_was_never_written_reports_nothing_rather_than_a_sentinel()
    {
        var registry = new MetricRegistry();
        registry.Gauge("untouched");

        var stats = registry.Global().Single();

        await Assert.That(stats.Min).IsEqualTo(0);
        await Assert.That(stats.Max).IsEqualTo(0);
        await Assert.That(stats.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Closing_an_interval_forgets_the_intervals_extremes_but_keeps_the_current_value()
    {
        var registry = new MetricRegistry();
        var gauge = registry.Gauge("pool");

        gauge.Set(100);
        registry.CloseInterval();

        gauge.Set(5);
        var second = registry.CloseInterval().Single();

        await Assert.That(second.Max).IsEqualTo(5);
        await Assert.That(registry.Global().Single().Max).IsEqualTo(100);
    }
}

internal class HistogramMetricTests
{
    [Test]
    public async Task A_histogram_reports_a_distribution()
    {
        var registry = new MetricRegistry();
        var histogram = registry.Histogram("batch", MetricUnit.Count);

        for (var i = 1; i <= 100; i++) histogram.Record(i);

        var stats = registry.Global().Single();

        await Assert.That(stats.Kind).IsEqualTo(MetricKind.Histogram);
        await Assert.That(stats.Count).IsEqualTo(100);
        await Assert.That(stats.Min).IsEqualTo(1);
        await Assert.That(stats.Max).IsEqualTo(100);
        await Assert.That(stats.Percent50).IsBetween(45, 55);
        await Assert.That(stats.Percent99).IsBetween(95, 100);
    }

    [Test]
    public async Task A_histogram_keeps_fractions()
    {
        var registry = new MetricRegistry();
        var histogram = registry.Histogram("ratio");

        histogram.Record(0.25);
        histogram.Record(0.75);

        var stats = registry.Global().Single();

        await Assert.That(stats.Min).IsEqualTo(0.25);
        await Assert.That(stats.Max).IsEqualTo(0.75);
    }

    [Test]
    public async Task A_negative_recording_is_pinned_rather_than_taking_the_run_with_it()
    {
        var registry = new MetricRegistry();
        var histogram = registry.Histogram("skew");

        histogram.Record(-5);
        histogram.Record(10);

        var stats = registry.Global().Single();

        await Assert.That(stats.Count).IsEqualTo(2);
        await Assert.That(stats.Min).IsEqualTo(0);
        await Assert.That(stats.Max).IsEqualTo(10);
    }
}

[NotInParallel]
internal class MetricsInARunTests
{
    [Test]
    public async Task A_scenario_can_write_a_metric_and_read_it_off_the_result()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("metered", async ctx =>
                    {
                        ctx.Metrics.Counter("orders.placed").Increment();
                        ctx.Metrics.Histogram("orders.size", MetricUnit.Count).Record(ctx.InvocationNumber % 10);

                        await Task.Delay(Time.Milliseconds(5));
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.IterationsForConstant(copies: 2, iterations: 100)))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .Run();

        var placed = stats.Metrics.Single(x => x.Name == "orders.placed");
        var size = stats.Metrics.Single(x => x.Name == "orders.size");

        await Assert.That(placed.Current).IsEqualTo(100);
        await Assert.That(size.Count).IsEqualTo(100);
        await Assert.That(stats.Metrics.Select(x => x.Name)).IsEquivalentTo(new[] { "orders.placed", "orders.size" });
    }

    [Test]
    public async Task A_scenario_can_register_its_metrics_from_init()
    {
        ICounter? fromInit = null;

        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("metered", async ctx =>
                    {
                        fromInit!.Add(2);
                        await Task.Delay(Time.Milliseconds(5));
                        return Response.Ok();
                    })
                    .WithInit(ctx =>
                    {
                        fromInit = ctx.Metrics.Counter("cache.miss");
                        return Task.CompletedTask;
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.IterationsForConstant(copies: 1, iterations: 20)))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .Run();

        await Assert.That(stats.Metrics.Single().Name).IsEqualTo("cache.miss");
        await Assert.That(stats.Metrics.Single().Current).IsEqualTo(40);
    }

    [Test]
    [Category("slow")]
    public async Task The_runtime_metrics_are_collected_without_anyone_asking()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("busy", async ctx =>
                    {
                        await Task.Delay(Time.Milliseconds(10), ctx.CancellationToken);
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 4, during: Time.Seconds(6))))
            .WithoutReports()
            .Run();

        var names = stats.Metrics.Select(x => x.Name).ToArray();

        await Assert.That(names).Contains(Constants.MetricCpuPercent);
        await Assert.That(names).Contains(Constants.MetricWorkingSet);
        await Assert.That(names).Contains(Constants.MetricGcHeap);
        await Assert.That(names).Contains(Constants.MetricThreadPoolQueue);
        await Assert.That(names).Contains(Constants.MetricThreads);

        // Sampled repeatedly over the run, not once at the end.
        await Assert.That(stats.Metrics.Single(x => x.Name == Constants.MetricWorkingSet).Count).IsGreaterThan(1);
        await Assert.That(stats.Metrics.Single(x => x.Name == Constants.MetricWorkingSet).Current).IsGreaterThan(0);

        // Ordered by name, so a diff between two runs is a diff of values.
        await Assert.That(names).IsEquivalentTo(names.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Test]
    public async Task Turning_the_runtime_metrics_off_leaves_only_what_the_scenario_wrote()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("quiet", async ctx =>
                    {
                        ctx.Metrics.Counter("mine").Increment();
                        await Task.Delay(Time.Milliseconds(5));
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.IterationsForConstant(copies: 1, iterations: 10)))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .Run();

        await Assert.That(stats.Metrics.Select(x => x.Name)).IsEquivalentTo(new[] { "mine" });
    }

    [Test]
    [Category("slow")]
    public async Task Interval_metrics_land_in_the_timeline_beside_the_scenario_stats()
    {
        var result = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("timeline", async ctx =>
                    {
                        ctx.Metrics.Counter("ticks").Increment();
                        await Task.Delay(Time.Milliseconds(10), ctx.CancellationToken);
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 2, during: Time.Seconds(12))))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .WithReportingInterval(Time.Seconds(5))
            .RunWithResult();

        var withMetrics = result.TimeLineHistory.Where(x => x.Metrics.Length > 0).ToArray();

        await Assert.That(withMetrics).IsNotEmpty();
        await Assert.That(withMetrics.All(x => x.Metrics.All(m => m.Name == "ticks"))).IsTrue();

        // Each interval reports its own slice, and the slices add up to the run's total.
        var intervalTotal = withMetrics.Sum(x => x.Metrics.Single().Current);
        await Assert.That(intervalTotal).IsLessThanOrEqualTo(result.FinalStats.Metrics.Single().Current);
        await Assert.That(intervalTotal).IsGreaterThan(0);
    }

    [Test]
    [Category("slow")]
    public async Task Metrics_reach_every_report_format()
    {
        var reportFolder = Path.Combine(Path.GetTempPath(), $"autobahn_metrics_{Guid.NewGuid():N}");

        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("reported", async ctx =>
                    {
                        ctx.Metrics.Counter("widgets").Increment();
                        await Task.Delay(Time.Milliseconds(5));
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.IterationsForConstant(copies: 2, iterations: 50)))
            .WithReportFolder(reportFolder)
            .WithoutRuntimeMetrics()
            .Run();

        var files = stats.ReportFiles.ToDictionary(x => Path.GetFileName(x.FilePath), x => x.ReportContent);

        // The step CSV is one row per step and deliberately carries no metrics; they get
        // their own file beside it. Every other format renders them inline.
        await Assert.That(files.Keys.Any(x => x.EndsWith("_metrics.csv"))).IsTrue();

        foreach (var (name, content) in files.Where(x => !x.Key.EndsWith(".csv") || x.Key.EndsWith("_metrics.csv")))
            await Assert.That(content).Contains("widgets").Because($"{name} should carry the metric");

        Directory.Delete(reportFolder, recursive: true);
    }
}
