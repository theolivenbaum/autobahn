using System.Text.Json;
using Autobahn.Metrics;
using Autobahn.Stats;
using Autobahn.Thresholds;
using Microsoft.Extensions.Logging;

namespace Autobahn.Tests;

[NotInParallel]
public class RunArtifactTests
{
    private static ScenarioProps TwoSteps(string name) =>
        Scenario.Create(name, async ctx =>
            {
                await Step.Run("first", ctx, async () =>
                {
                    await Task.Delay(Time.Milliseconds(5));
                    return Response.Ok(statusCode: "200", sizeBytes: 128);
                });

                ctx.Metrics.Counter("widgets").Increment();

                return await Step.Run("second", ctx, async () =>
                {
                    await Task.Delay(Time.Milliseconds(5));
                    return Response.Ok(statusCode: "200", sizeBytes: 64);
                });
            })
            .WithoutWarmUp()
            .WithLoadSimulations(
                Simulation.IterationsForConstant(copies: 2, iterations: 40),
                Simulation.Pause(Time.Seconds(1)));

    private static (JsonDocument Doc, string Folder) Run(string folder)
    {
        var stats = AutobahnRunner
            .RegisterScenarios(TwoSteps("artifact"))
            .WithReportFolder(folder)
            .WithReportFormats(ReportFormat.Json)
            .WithoutRuntimeMetrics()
            .WithThresholds(Threshold.ErrorRateBelow(0.01).Named("stays reliable"))
            .Run();

        var json = stats.ReportFiles.Single(x => x.ReportFormat == ReportFormat.Json).ReportContent;
        return (JsonDocument.Parse(json), folder);
    }

    [Test]
    public async Task The_run_artifact_is_written_as_a_versioned_document()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"autobahn_artifact_{Guid.NewGuid():N}");
        var (doc, _) = Run(folder);

        var root = doc.RootElement;

        await Assert.That(root.GetProperty("SchemaVersion").GetInt32())
            .IsEqualTo(Constants.RunArtifactSchemaVersion);

        await Assert.That(root.GetProperty("Producer").GetString()).StartsWith("Autobahn ");
        await Assert.That(root.TryGetProperty("CompletedAt", out _)).IsTrue();

        Directory.Delete(folder, recursive: true);
    }

    [Test]
    public async Task The_run_artifact_carries_the_whole_result()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"autobahn_artifact_{Guid.NewGuid():N}");
        var (doc, _) = Run(folder);

        var result = doc.RootElement.GetProperty("Result");
        var finalStats = result.GetProperty("FinalStats");

        await Assert.That(finalStats.GetProperty("ScenarioStats").GetArrayLength()).IsEqualTo(1);

        var scenario = finalStats.GetProperty("ScenarioStats")[0];

        // 40 iterations, and each ran both its steps.
        await Assert.That(scenario.GetProperty("Ok").GetProperty("Request").GetProperty("Count").GetInt32())
            .IsEqualTo(40);

        // The artifact records the run as measured, not as the reports render it: the reports
        // fold the scenario's own numbers in as an extra pseudo-step, and the artifact does not.
        var steps = scenario.GetProperty("StepStats");
        await Assert.That(steps.GetArrayLength()).IsEqualTo(2);
        await Assert.That(steps[0].GetProperty("StepName").GetString()).IsEqualTo("first");

        await Assert.That(finalStats.GetProperty("Metrics").GetArrayLength()).IsEqualTo(1);
        await Assert.That(finalStats.GetProperty("Metrics")[0].GetProperty("Name").GetString()).IsEqualTo("widgets");

        await Assert.That(finalStats.GetProperty("Thresholds").GetArrayLength()).IsEqualTo(1);
        await Assert.That(finalStats.GetProperty("Thresholds")[0].GetProperty("Passed").GetBoolean()).IsTrue();

        await Assert.That(result.TryGetProperty("TimeLineHistory", out _)).IsTrue();
        await Assert.That(result.TryGetProperty("Hints", out _)).IsTrue();

        Directory.Delete(folder, recursive: true);
    }

    [Test]
    public async Task The_run_artifact_records_the_load_plan_each_scenario_ran()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"autobahn_artifact_{Guid.NewGuid():N}");
        var (doc, _) = Run(folder);

        var plans = doc.RootElement.GetProperty("Plans");

        await Assert.That(plans.GetArrayLength()).IsEqualTo(1);
        await Assert.That(plans[0].GetProperty("ScenarioName").GetString()).IsEqualTo("artifact");
        await Assert.That(plans[0].GetProperty("LoadSimulations").GetArrayLength()).IsEqualTo(2);

        Directory.Delete(folder, recursive: true);
    }

    [Test]
    public async Task The_run_artifact_is_written_alongside_every_other_format_by_default()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"autobahn_artifact_{Guid.NewGuid():N}");

        var stats = AutobahnRunner
            .RegisterScenarios(TwoSteps("artifact"))
            .WithReportFolder(folder)
            .WithoutRuntimeMetrics()
            .Run();

        var extensions = stats.ReportFiles.Select(x => Path.GetExtension(x.FilePath)).Distinct().ToArray();

        await Assert.That(extensions).Contains(".json");
        await Assert.That(Directory.EnumerateFiles(folder, "*.json")).IsNotEmpty();

        Directory.Delete(folder, recursive: true);
    }

    [Test]
    public async Task The_run_artifact_is_indented_so_it_diffs()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"autobahn_artifact_{Guid.NewGuid():N}");

        var stats = AutobahnRunner
            .RegisterScenarios(TwoSteps("artifact"))
            .WithReportFolder(folder)
            .WithReportFormats(ReportFormat.Json)
            .WithoutRuntimeMetrics()
            .Run();

        var json = stats.ReportFiles.Single().ReportContent;

        await Assert.That(json.Split('\n').Length).IsGreaterThan(50);

        Directory.Delete(folder, recursive: true);
    }
}

[NotInParallel]
public class ReportFolderTests
{
    private static ScenarioProps Trivial() =>
        Scenario.Create("trivial", async ctx =>
            {
                await Task.Delay(Time.Milliseconds(5), ctx.CancellationToken);
                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.IterationsForConstant(copies: 1, iterations: 10));

    [Test]
    public async Task A_pinned_report_folder_is_not_wiped_between_runs()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"autobahn_pinned_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        // Something a previous run - or a person - left in the folder Autobahn was pointed at.
        var bystander = Path.Combine(folder, "please-do-not-delete-me.txt");
        await File.WriteAllTextAsync(bystander, "important");

        var subfolder = Path.Combine(folder, "nested");
        Directory.CreateDirectory(subfolder);
        await File.WriteAllTextAsync(Path.Combine(subfolder, "also-important.txt"), "important");

        AutobahnRunner
            .RegisterScenarios(Trivial())
            .WithReportFolder(folder)
            .WithReportFileName("first")
            .WithoutRuntimeMetrics()
            .Run();

        AutobahnRunner
            .RegisterScenarios(Trivial())
            .WithReportFolder(folder)
            .WithReportFileName("second")
            .WithoutRuntimeMetrics()
            .Run();

        await Assert.That(File.Exists(bystander)).IsTrue();
        await Assert.That(File.Exists(Path.Combine(subfolder, "also-important.txt"))).IsTrue();

        // Both runs' reports are still there; they carry their own names and accumulate.
        await Assert.That(Directory.EnumerateFiles(folder, "first.*")).IsNotEmpty();
        await Assert.That(Directory.EnumerateFiles(folder, "second.*")).IsNotEmpty();

        Directory.Delete(folder, recursive: true);
    }
}

[NotInParallel]
public class IntervalBoundaryTests
{
    /// <summary>
    /// A run long enough for several reporting intervals, at a rate that makes each one's
    /// request count a statement about the window it covers.
    /// </summary>
    private static ScenarioProps Steady(string name, TimeSpan during) =>
        Scenario.Create(name, async ctx =>
            {
                await Task.Delay(Time.Milliseconds(10), ctx.CancellationToken);
                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.Inject(rate: 20, interval: Time.Seconds(1), during: during));

    [Test]
    [Category("slow")]
    public async Task Every_emitted_interval_covers_a_full_window()
    {
        var result = AutobahnRunner
            .RegisterScenarios(Steady("steady", Time.Seconds(25)))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .WithReportingInterval(Time.Seconds(5))
            .RunWithResult();

        var intervals = result.TimeLineHistory;

        // The timer starts with the run, so the intervals are 5s, 10s, 15s… with nothing
        // skipped and nothing offset by a fixed start delay.
        await Assert.That(intervals.Length).IsGreaterThanOrEqualTo(4);
        await Assert.That(intervals.Select(x => x.Duration))
            .IsEquivalentTo(Enumerable.Range(1, intervals.Length).Select(i => Time.Seconds(5 * i)).ToArray());

        // Injecting 20/s for five seconds is 100 requests a window. The first window is the
        // one the old fixed start delay stretched to eight seconds' worth of traffic, so it
        // is the one worth pinning: every window should look like every other.
        var counts = intervals.Select(x => x.ScenarioStats.Single().AllOkCount).ToArray();

        foreach (var count in counts.Take(intervals.Length - 1))
            await Assert.That(count).IsBetween(70, 130);
    }
}

[NotInParallel]
public class NonInteractiveConsoleTests
{
    [Test]
    [Category("slow")]
    public async Task Interval_progress_is_logged_as_plain_lines_when_there_is_no_terminal()
    {
        var logs = new InMemoryLoggerProvider();

        AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("plain", async ctx =>
                    {
                        await Task.Delay(Time.Milliseconds(10), ctx.CancellationToken);
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.Inject(rate: 20, interval: Time.Seconds(1), during: Time.Seconds(12))))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .WithReportingInterval(Time.Seconds(5))
            .WithLogging(builder => builder.AddProvider(logs))
            .Run("disposeLogger=false");

        // The tests run with output redirected, which is the CI-log case: no live table, one
        // plain line per scenario per interval instead.
        await Assert.That(logs.HasMessageContaining("[00:00:05] plain:")).IsTrue();
        await Assert.That(logs.HasMessageContaining("[00:00:10] plain:")).IsTrue();
        await Assert.That(logs.HasMessageContaining("ok ")).IsTrue();
        await Assert.That(logs.HasMessageContaining("p99 ")).IsTrue();
    }
}
