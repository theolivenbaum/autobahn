using System.Text.Json;
using Autobahn.Cli.Ui;
using Autobahn.Stats;
using Autobahn.Thresholds;
using Autobahn.Ui.Contracts;

namespace Autobahn.Tests;

/// <summary>
/// The seams the live UI reads a run through, and the frames it turns them into.
/// </summary>
/// <remarks>
/// None of this starts a web server. The promise the UI is under is that a run behaves
/// identically whether or not anyone is watching, and the way that promise is kept is that
/// the engine only ever hands out records - so what is worth testing is the records.
/// </remarks>
internal class SessionStartObserverTests
{
    [Test]
    [NotInParallel]
    public async Task The_run_says_what_it_resolved_before_it_starts()
    {
        SessionStartInfo? start = null;

        var scenario = Scenario.Create("observed", _ => Task.FromResult<IResponse>(Response.Ok()))
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(copies: 2, during: Time.Seconds(2)));

        AutobahnRunner
            .RegisterScenarios(scenario)
            .WithTestSuite("observed suite")
            .WithTestName("observed test")
            .WithReportingInterval(Time.Seconds(5))
            .WithThresholds(Threshold.ErrorRateBelow(0.5))
            .WithoutCancelKeyPress()
            .WithSessionStartObserver(info =>
            {
                start = info;
                return Task.CompletedTask;
            })
            .Run();

        await Assert.That(start).IsNotNull();
        await Assert.That(start!.TestInfo.TestSuite).IsEqualTo("observed suite");
        await Assert.That(start.TestInfo.TestName).IsEqualTo("observed test");
        await Assert.That(start.ReportingInterval).IsEqualTo(Time.Seconds(5));
        await Assert.That(start.Thresholds.Count).IsEqualTo(1);
        await Assert.That(start.ReportFolder).IsNotEmpty();
    }

    [Test]
    [NotInParallel]
    public async Task It_carries_the_plan_each_scenario_will_actually_run()
    {
        SessionStartInfo? start = null;

        var scenario = Scenario.Create("planned", _ => Task.FromResult<IResponse>(Response.Ok()))
            .WithoutWarmUp()
            .WithLoadSimulations(
                Simulation.RampingConstant(copies: 4, during: Time.Seconds(1)),
                Simulation.KeepConstant(copies: 4, during: Time.Seconds(1)));

        AutobahnRunner
            .RegisterScenarios(scenario)
            .WithoutCancelKeyPress()
            .WithSessionStartObserver(info =>
            {
                start = info;
                return Task.CompletedTask;
            })
            .Run();

        await Assert.That(start).IsNotNull();
        await Assert.That(start!.Scenarios.Count).IsEqualTo(1);

        var planned = start.Scenarios[0];

        await Assert.That(planned.ScenarioName).IsEqualTo("planned");
        await Assert.That(planned.LoadSimulations.Count).IsEqualTo(2);
        await Assert.That(planned.MaxCopies).IsEqualTo(4);
        await Assert.That(planned.PlannedDuration).IsEqualTo(Time.Seconds(2));
    }

    /// <summary>
    /// The whole reason the observer exists: a caller holding the context knows what it asked
    /// for, not what the layers below it settled on.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task The_settings_it_reports_are_the_resolved_ones()
    {
        SessionStartInfo? start = null;

        var scenario = Scenario.Create("resolved", _ => Task.FromResult<IResponse>(Response.Ok()))
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(1)));

        AutobahnRunner
            .RegisterScenarios(scenario)
            .WithTestName("from code")
            .WithoutCancelKeyPress()
            .WithSessionStartObserver(info =>
            {
                start = info;
                return Task.CompletedTask;
            })
            .Run();

        await Assert.That(start).IsNotNull();

        var name = start!.EffectiveSettings.FirstOrDefault(x => x.Name == "TestName");

        await Assert.That(name).IsNotNull();
        await Assert.That(name!.Value).IsEqualTo("from code");
        await Assert.That(name.Source).IsEqualTo(Configuration.ConfigSource.Code);
    }

    /// <summary>A broken watcher is not a reason to lose a test.</summary>
    [Test]
    [NotInParallel]
    public async Task An_observer_that_throws_does_not_stop_the_run()
    {
        var scenario = Scenario.Create("survives", _ => Task.FromResult<IResponse>(Response.Ok()))
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(1)));

        var result = AutobahnRunner
            .RegisterScenarios(scenario)
            .WithoutCancelKeyPress()
            .WithSessionStartObserver(_ => throw new InvalidOperationException("the watcher broke"))
            .Run();

        await Assert.That(result.AllOkCount).IsGreaterThan(0);
    }
}

internal class LiveIntervalTests
{
    /// <summary>
    /// A threshold that passed, failed and recovered is a different run from one that failed at
    /// the end, and the timeline is the only place that difference exists.
    /// </summary>
    [Test]
    [NotInParallel]
    [Category("slow")]
    public async Task Each_interval_carries_where_the_thresholds_stood()
    {
        var intervals = new List<TimeLineHistoryRecord>();

        var scenario = Scenario.Create("gated", _ => Task.FromResult<IResponse>(Response.Ok()))
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(copies: 2, during: Time.Seconds(12)));

        try
        {
            AutobahnRunner
                .RegisterScenarios(scenario)
                .WithReportingInterval(Time.Seconds(5))
                .WithThresholds(Threshold.ErrorRateBelow(0.5))
                .WithoutThresholdExitCode()
                .WithoutCancelKeyPress()
                .WithIntervalObserver(record =>
                {
                    lock (intervals) intervals.Add(record);
                    return Task.CompletedTask;
                })
                .Run();
        }
        finally
        {
            Environment.ExitCode = 0;
        }

        await Assert.That(intervals.Count).IsGreaterThan(0);
        await Assert.That(intervals.All(x => x.Thresholds.Length == 1)).IsTrue();
        await Assert.That(intervals.Last().Thresholds[0].Passed).IsTrue();
    }

    /// <summary>
    /// Scheduled against actual is the clearest signal that the generator is saturated rather
    /// than the target, which only works if the two are measured separately.
    /// </summary>
    [Test]
    [NotInParallel]
    [Category("slow")]
    public async Task Each_interval_reports_the_copies_that_were_really_live()
    {
        var intervals = new List<TimeLineHistoryRecord>();

        var scenario = Scenario.Create("busy", async context =>
            {
                await Task.Delay(Time.Seconds(0.2), context.CancellationToken);
                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(copies: 8, during: Time.Seconds(12)));

        AutobahnRunner
            .RegisterScenarios(scenario)
            .WithReportingInterval(Time.Seconds(5))
            .WithoutCancelKeyPress()
            .WithIntervalObserver(record =>
            {
                lock (intervals) intervals.Add(record);
                return Task.CompletedTask;
            })
            .Run();

        await Assert.That(intervals.Count).IsGreaterThan(1);

        var live = intervals.Select(x => x.ScenarioStats[0].LoadSimulationStats.ActualCopies).ToArray();

        // Every copy sleeps for a fifth of a second in a one second interval, so at least one
        // of them is mid-iteration whenever an interval closes.
        await Assert.That(live.Any(x => x > 0)).IsTrue();
        await Assert.That(live.All(x => x <= 8)).IsTrue();
    }
}

internal class FrameBuilderTests
{
    [Test]
    public async Task A_scenario_frame_keeps_scheduled_and_actual_apart()
    {
        var record = new TimeLineHistoryRecord
        {
            Duration = TimeSpan.FromSeconds(5),
            ScenarioStats =
            [
                Statistics(scheduled: 100, actual: 40)
            ]
        };

        var frame = FrameBuilder.Frame(
            record, RunState.Bombing, "running", 1_700_000_000_000, [], []);

        await Assert.That(frame.Scenarios.Length).IsEqualTo(1);
        await Assert.That(frame.Scenarios[0].ScheduledCopies).IsEqualTo(100);
        await Assert.That(frame.Scenarios[0].ActualCopies).IsEqualTo(40);
    }

    [Test]
    public async Task A_ramp_records_where_it_came_from_so_it_can_be_drawn_as_one()
    {
        var plan = FrameBuilder.Plan(
        [
            Simulation.KeepConstant(copies: 5, during: Time.Seconds(10)),
            Simulation.RampingConstant(copies: 50, during: Time.Seconds(20))
        ]);

        await Assert.That(plan.Length).IsEqualTo(2);

        await Assert.That(plan[0].StartSeconds).IsEqualTo(0);
        await Assert.That(plan[0].Level).IsEqualTo(5);
        await Assert.That(plan[0].FromLevel).IsEqualTo(5);

        await Assert.That(plan[1].StartSeconds).IsEqualTo(10);
        await Assert.That(plan[1].Level).IsEqualTo(50);
        await Assert.That(plan[1].FromLevel).IsEqualTo(5);
    }

    /// <summary>A counted segment has no length to put on a timeline, and must not invent one.</summary>
    [Test]
    public async Task A_counted_segment_has_no_duration()
    {
        var plan = FrameBuilder.Plan([Simulation.IterationsForConstant(copies: 2, iterations: 100)]);

        await Assert.That(plan.Length).IsEqualTo(1);
        await Assert.That(plan[0].DurationSeconds).IsNull();
        await Assert.That(plan[0].Iterations).IsEqualTo(100);
    }

    private static ScenarioStats Statistics(int scheduled, int actual) => new()
    {
        ScenarioName = "scenario",
        Ok = MeasurementStats.Empty,
        Fail = MeasurementStats.Empty,
        StepStats = [],
        LoadSimulationStats = new LoadSimulationStats
        {
            SimulationName = "keep_constant",
            Value = scheduled,
            ActualCopies = actual
        },
        CurrentOperation = OperationType.Bombing,
        AllRequestCount = 0,
        AllOkCount = 0,
        AllFailCount = 0,
        AllBytes = 0,
        Duration = TimeSpan.FromSeconds(5)
    };
}

internal class RunFeedTests
{
    [Test]
    public async Task Frames_are_numbered_from_one()
    {
        var feed = new RunFeed(UiOptions.Default);

        feed.Publish(new LiveFrame { ElapsedSeconds = 1 });
        feed.Publish(new LiveFrame { ElapsedSeconds = 2 });

        var snapshot = feed.Snapshot();

        await Assert.That(snapshot.History.Length).IsEqualTo(2);
        await Assert.That(snapshot.History[0].Sequence).IsEqualTo(1);
        await Assert.That(snapshot.History[1].Sequence).IsEqualTo(2);
        await Assert.That(snapshot.Latest!.Sequence).IsEqualTo(2);
    }

    [Test]
    public async Task History_backfills_from_a_sequence_number()
    {
        var feed = new RunFeed(UiOptions.Default);

        for (var i = 0; i < 5; i++) feed.Publish(new LiveFrame { ElapsedSeconds = i });

        var history = feed.History(3);

        await Assert.That(history.Frames.Length).IsEqualTo(3);
        await Assert.That(history.Frames[0].Sequence).IsEqualTo(3);
        await Assert.That(history.OldestSequence).IsEqualTo(1);
    }

    /// <summary>
    /// A client that has fallen off the back of the ring has to be told, or it would stitch a
    /// gap it cannot see.
    /// </summary>
    [Test]
    public async Task A_client_that_asks_for_too_far_back_is_told_how_far_back_there_is()
    {
        var feed = new RunFeed(new UiOptions { HistoryCapacity = 4 });

        for (var i = 0; i < 10; i++) feed.Publish(new LiveFrame { ElapsedSeconds = i });

        var history = feed.History(1);

        await Assert.That(history.OldestSequence).IsEqualTo(7);
        await Assert.That(history.Frames.All(x => x.Sequence >= 7)).IsTrue();
    }

    /// <summary>
    /// A slow client drops frames rather than applying back-pressure to the run, which is the
    /// whole reason this sits between the two.
    /// </summary>
    [Test]
    public async Task A_subscriber_that_never_reads_does_not_hold_up_a_publisher()
    {
        var feed = new RunFeed(new UiOptions { ClientQueueCapacity = 2 });

        using var subscriber = feed.Subscribe();

        for (var i = 0; i < 1_000; i++) feed.Publish(new LiveFrame { ElapsedSeconds = i });

        await Assert.That(feed.Snapshot().Latest!.Sequence).IsEqualTo(1_000);
    }
}

internal class PastRunsTests
{
    /// <summary>
    /// The comparison screen reads previous runs out of the folder beside this one, from the
    /// run artifact and nothing else.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task Previous_runs_are_read_back_from_their_artifacts()
    {
        var root = Directory.CreateTempSubdirectory("autobahn-runs");

        try
        {
            var first = Write(root.FullName, "run-a");
            var second = Write(root.FullName, "run-b");

            var runs = PastRuns.List(Path.Combine(root.FullName, "run-b"), second);

            await Assert.That(runs.Length).IsEqualTo(2);
            await Assert.That(runs.Select(x => x.Id)).Contains("run-a");
            await Assert.That(runs.Select(x => x.Id)).Contains("run-b");

            // Newest first, because that is the one somebody is comparing against.
            await Assert.That(runs[0].CompletedAtEpochMs).IsGreaterThanOrEqualTo(runs[1].CompletedAtEpochMs);

            var current = runs.Single(x => x.Id == "run-b");

            await Assert.That(current.IsCurrent).IsTrue();
            await Assert.That(current.Ok).IsGreaterThan(0);
            await Assert.That(current.ThresholdsPassed).IsTrue();
            await Assert.That(runs.Single(x => x.Id == "run-a").IsCurrent).IsFalse();
            await Assert.That(first).IsNotEqualTo(second);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    [NotInParallel]
    public async Task One_run_comes_back_with_its_scenarios_and_steps()
    {
        var root = Directory.CreateTempSubdirectory("autobahn-runs");

        try
        {
            Write(root.FullName, "run-a");

            var detail = PastRuns.Detail(Path.Combine(root.FullName, "run-a"), "run-a", "whatever");

            await Assert.That(detail).IsNotNull();
            await Assert.That(detail!.Scenarios.Length).IsEqualTo(1);
            await Assert.That(detail.Scenarios[0].ScenarioName).IsEqualTo("compared");
            await Assert.That(detail.Scenarios[0].Ok.Count).IsGreaterThan(0);
            await Assert.That(detail.Scenarios[0].Steps.Length).IsEqualTo(1);
            await Assert.That(detail.Scenarios[0].Steps[0].StepName).IsEqualTo("only");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>The id names a folder and comes off the wire, so it is checked rather than trusted.</summary>
    [Test]
    [NotInParallel]
    [Arguments("../elsewhere")]
    [Arguments("nested/deeper")]
    [Arguments("")]
    public async Task An_id_that_is_not_a_folder_name_is_refused(string id)
    {
        var root = Directory.CreateTempSubdirectory("autobahn-runs");

        try
        {
            Write(root.FullName, "run-a");

            await Assert.That(PastRuns.Detail(Path.Combine(root.FullName, "run-a"), id, "session")).IsNull();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>A report folder is the user's; a json file in it is not necessarily an artifact.</summary>
    [Test]
    public async Task Json_that_is_not_a_run_artifact_is_ignored()
    {
        var root = Directory.CreateTempSubdirectory("autobahn-runs");

        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "not-a-run"));
            await File.WriteAllTextAsync(Path.Combine(folder.FullName, "notes.json"), """{ "hello": "world" }""");

            await Assert.That(PastRuns.List(Path.Combine(root.FullName, "not-a-run"), "session").Length).IsEqualTo(0);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A real run into a named folder, because the reader is the engine's own artifact reader.
    /// </summary>
    /// <remarks>
    /// Hand-rolling the json was tried and is the wrong test: the document's shape is the
    /// engine's records, so a hand-written one either satisfies every required member - at
    /// which point it is a worse copy of the writer - or is rejected, and the test measures the
    /// fixture rather than the reader.
    /// </remarks>
    /// <returns>The session id of the run that was written.</returns>
    private static string Write(string root, string id)
    {
        var scenario = Scenario.Create("compared", async context =>
                await Step.Run("only", context, async () =>
                {
                    await Task.Delay(Time.Milliseconds(2), context.CancellationToken);
                    return Response.Ok(statusCode: "200", sizeBytes: 64);
                }))
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.IterationsForConstant(copies: 2, iterations: 20));

        var stats = AutobahnRunner
            .RegisterScenarios(scenario)
            .WithReportFolder(Path.Combine(root, id))
            .WithReportFormats(ReportFormat.Json)
            .WithThresholds(Threshold.ErrorRateBelow(0.5))
            .WithoutCancelKeyPress()
            .Run();

        return stats.TestInfo.SessionId;
    }
}

internal class StaticExportTests
{
    /// <summary>
    /// The exported page is the live application handed a snapshot from a file, so what is
    /// worth testing is that the artifact becomes the same snapshot the host would have served.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task An_artifact_becomes_the_snapshot_the_live_host_serves()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"autobahn_export_{Guid.NewGuid():N}");

        try
        {
            var artifact = RunAndRead(folder);
            var snapshot = ArtifactSnapshot.Build(artifact, folder);

            await Assert.That(snapshot.Run.TestName).IsEqualTo(artifact.Result.FinalStats.TestInfo.TestName);
            await Assert.That(snapshot.Run.Scenarios.Length).IsEqualTo(1);
            await Assert.That(snapshot.Run.Scenarios[0].Plan.Length).IsGreaterThan(0);

            // The last frame is the run's verdict, not its last interval.
            await Assert.That(snapshot.Latest).IsNotNull();
            await Assert.That(snapshot.Latest!.State).IsEqualTo(RunState.Finished);
            await Assert.That(snapshot.Latest.StatusText).StartsWith("Finished.");
            await Assert.That(snapshot.Latest.Scenarios.Length).IsEqualTo(1);
            await Assert.That(snapshot.Latest.Thresholds.Length).IsEqualTo(1);

            // The reports beside the artifact, named so a reader knows what else the run wrote.
            await Assert.That(snapshot.Reports.Length).IsGreaterThan(0);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Frames are numbered, because the client detects gaps by sequence number.</summary>
    [Test]
    [NotInParallel]
    public async Task The_replayed_history_is_numbered_in_order()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"autobahn_export_{Guid.NewGuid():N}");

        try
        {
            var snapshot = ArtifactSnapshot.Build(RunAndRead(folder), folder);

            for (var i = 0; i < snapshot.History.Length; i++)
            {
                await Assert.That(snapshot.History[i].Sequence).IsEqualTo(i + 1);
            }

            await Assert.That(snapshot.Latest!.Sequence).IsEqualTo(snapshot.History.Length + 1);

            // The artifact's timeline carries where each threshold stood in each interval, so
            // the exported page's pass/fail strip is the one the live view showed.
            await Assert.That(snapshot.History.All(x => x.Thresholds.Length == 1)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The whole page, in one file, when this build has a UI to put in it.
    /// </summary>
    /// <remarks>
    /// Conditional because a clean clone builds the CLI without the compiled application - that
    /// is the point of keeping the Transpose compiler out of the default build - and a test
    /// that failed there would be reporting on the build rather than on the code.
    /// </remarks>
    [Test]
    [NotInParallel]
    public async Task It_writes_one_self_contained_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"autobahn_export_{Guid.NewGuid():N}");

        try
        {
            RunAndRead(folder);

            var artifactPath = Directory.EnumerateFiles(folder, "*.json").Single();
            var target = Path.Combine(folder, "export.html");

            var written = StaticExport.Write(artifactPath, target);

            if (!UiAssets.IsBuilt)
            {
                await Assert.That(written).IsNull();
                return;
            }

            await Assert.That(written).IsEqualTo(target);

            var html = await File.ReadAllTextAsync(target);

            await Assert.That(html).Contains("window.__autobahnSnapshot");

            // Self-contained: no element left pointing at a file that will not travel with it,
            // and no font url that is not the font itself.
            await Assert.That(html).DoesNotContain("<script src=");
            await Assert.That(html).DoesNotContain("<link ");
            await Assert.That(html).DoesNotContain(".woff2)");
            await Assert.That(html).Contains("data:font/woff2;base64,");
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task Anything_that_is_not_an_artifact_is_refused()
    {
        var folder = Directory.CreateTempSubdirectory("autobahn-export");

        try
        {
            var path = Path.Combine(folder.FullName, "notes.json");
            await File.WriteAllTextAsync(path, """{ "hello": "world" }""");

            await Assert.That(StaticExport.Write(path, null)).IsNull();
            await Assert.That(StaticExport.Write(Path.Combine(folder.FullName, "missing.json"), null)).IsNull();
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static RunArtifact RunAndRead(string folder)
    {
        var scenario = Scenario.Create("exported", async context =>
            {
                await Task.Delay(Time.Milliseconds(5), context.CancellationToken);
                return Response.Ok(statusCode: "200", sizeBytes: 128);
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.IterationsForConstant(copies: 2, iterations: 40));

        var stats = AutobahnRunner
            .RegisterScenarios(scenario)
            .WithReportFolder(folder)
            .WithReportFormats(ReportFormat.Json, ReportFormat.Txt)
            .WithThresholds(Threshold.ErrorRateBelow(0.5).Named("stays reliable"))
            .WithoutCancelKeyPress()
            .Run();

        var json = stats.ReportFiles.Single(x => x.ReportFormat == ReportFormat.Json).ReportContent;

        return RunArtifact.TryRead(json, out var artifact)
            ? artifact
            : throw new InvalidOperationException("The run did not write a readable artifact.");
    }
}
