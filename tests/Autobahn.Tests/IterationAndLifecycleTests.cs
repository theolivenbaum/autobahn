using Autobahn.Stats;
using Microsoft.Extensions.Logging;

namespace Autobahn.Tests;

[NotInParallel]
public class IterationCountTests
{
    [Test]
    public async Task IterationsForConstant_runs_exactly_the_iterations_asked_for()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("counted", async _ =>
                    {
                        await Task.Delay(Time.Milliseconds(20));
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.IterationsForConstant(copies: 5, iterations: 50)))
            .WithoutReports()
            .Run();

        await Assert.That(stats.ScenarioStats[0].Ok.Request.Count).IsEqualTo(50);
        await Assert.That(stats.AllRequestCount).IsEqualTo(50);
    }

    [Test]
    public async Task IterationsForInject_runs_exactly_the_iterations_asked_for()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("counted", async _ =>
                    {
                        await Task.Delay(Time.Milliseconds(20));
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(
                        Simulation.IterationsForInject(rate: 10, interval: Time.Seconds(1), iterations: 25)))
            .WithoutReports()
            .Run();

        await Assert.That(stats.ScenarioStats[0].Ok.Request.Count).IsEqualTo(25);
    }

    [Test]
    public async Task A_single_iteration_is_a_valid_smoke_test()
    {
        var invocations = 0;

        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("smoke", _ =>
                    {
                        Interlocked.Increment(ref invocations);
                        return Task.FromResult<IResponse>(Response.Ok());
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.IterationsForConstant(copies: 1, iterations: 1)))
            .WithoutReports()
            .Run();

        await Assert.That(invocations).IsEqualTo(1);
        await Assert.That(stats.AllOkCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_counted_segment_can_be_followed_by_a_timed_one()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("mixed", async _ =>
                    {
                        await Task.Delay(Time.Milliseconds(50));
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(
                        Simulation.IterationsForConstant(copies: 2, iterations: 10),
                        Simulation.Inject(rate: 5, interval: Time.Seconds(1), during: Time.Seconds(2))))
            .WithoutReports()
            .Run();

        // 10 counted plus 5 per second for 2 seconds.
        await Assert.That(stats.ScenarioStats[0].Ok.Request.Count).IsEqualTo(20);
    }
}

[NotInParallel]
public class ScenarioLifecycleTests
{
    [Test]
    public async Task An_iteration_that_outruns_its_timeout_is_recorded_as_a_timeout()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("slow", async ctx =>
                    {
                        await Task.Delay(Time.Seconds(30), ctx.CancellationToken);
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithIterationTimeout(Time.Milliseconds(200))
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 2, during: Time.Seconds(3))))
            .WithoutReports()
            .Run();

        var scnStats = stats.GetScenarioStats("slow");

        // The iteration in flight when the scenario stops is cancelled rather than timed out,
        // so a shutdown '-100' can sit alongside the timeouts; the timeouts are the bulk of it.
        var timedOut = scnStats.Fail.StatusCodes
            .Single(x => x.StatusCode == Constants.IterationTimeoutStatusCode);

        await Assert.That(scnStats.Fail.Request.Count).IsGreaterThan(0);
        await Assert.That(timedOut.Count).IsGreaterThan(scnStats.Fail.Request.Count / 2);
        await Assert.That(timedOut.Message).IsEqualTo(Constants.IterationTimeoutMessage);
        await Assert.That(scnStats.Ok.Request.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_timed_out_iteration_is_told_to_stop_through_its_cancellation_token()
    {
        var cancelled = 0;

        AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("slow", async ctx =>
                    {
                        try
                        {
                            await Task.Delay(Time.Seconds(30), ctx.CancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            Interlocked.Increment(ref cancelled);
                            throw;
                        }

                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithIterationTimeout(Time.Milliseconds(200))
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(3))))
            .WithoutReports()
            .Run();

        await Assert.That(cancelled).IsGreaterThan(0);
    }

    [Test]
    public async Task A_step_that_outruns_its_own_timeout_is_recorded_as_a_timeout()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("slow-step", async ctx =>
                    {
                        await Step.Run("slow", ctx, async () =>
                        {
                            await Task.Delay(Time.Seconds(30), ctx.CancellationToken);
                            return Response.Ok();
                        }, timeout: Time.Milliseconds(200));

                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithRestartIterationOnFail(false)
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(2))))
            .WithoutReports()
            .Run();

        var step = stats.GetScenarioStats("slow-step").GetStepStats("slow");

        await Assert.That(step.Fail.Request.Count).IsGreaterThan(0);
        await Assert.That(step.Fail.StatusCodes[0].StatusCode).IsEqualTo(Constants.TimeoutStatusCode);
    }

    [Test]
    public async Task The_completion_hook_receives_the_scenario_final_stats()
    {
        ScenarioStats? seen = null;
        string? seenName = null;

        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("hooked", async _ =>
                    {
                        await Task.Delay(Time.Milliseconds(50));
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithCompletionHook(ctx =>
                    {
                        seen = ctx.Stats;
                        seenName = ctx.ScenarioInfo.ScenarioName;
                        return Task.CompletedTask;
                    })
                    .WithLoadSimulations(Simulation.IterationsForConstant(copies: 2, iterations: 20)))
            .WithoutReports()
            .Run();

        await Assert.That(seen).IsNotNull();
        await Assert.That(seenName).IsEqualTo("hooked");
        await Assert.That(seen!.Ok.Request.Count).IsEqualTo(stats.ScenarioStats[0].Ok.Request.Count);
    }

    [Test]
    public async Task A_completion_hook_that_throws_does_not_fail_the_run()
    {
        var result = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("hooked", _ => Task.FromResult<IResponse>(Response.Ok()))
                    .WithoutWarmUp()
                    .WithCompletionHook(_ => throw new InvalidOperationException("hook exploded"))
                    .WithLoadSimulations(Simulation.IterationsForConstant(copies: 1, iterations: 3)))
            .WithoutReports()
            .RunWithResult();

        await Assert.That(result.FinalStats.AllOkCount).IsEqualTo(3);
    }

    [Test]
    public async Task Iterations_still_running_when_the_completion_timeout_expires_are_reported()
    {
        var logs = new InMemoryLoggerProvider();

        AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("stubborn", async _ =>
                    {
                        // Deliberately ignores the cancellation token, so it can only be abandoned.
                        await Task.Delay(Time.Seconds(20));
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithCompletionTimeout(Time.Milliseconds(500))
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 3, during: Time.Seconds(2))))
            .WithLogging(builder => builder.AddProvider(logs))
            .WithoutReports()
            .Run("disposeLogger=false");

        await Assert.That(logs.HasMessageContaining("abandoned")).IsTrue();
        await Assert.That(logs.HasMessageContaining("stubborn")).IsTrue();
    }
}

[NotInParallel]
public class InstanceAwareDistributionTests
{
    [Test]
    public async Task Each_copy_sees_the_plans_maximum_copy_count()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<(int Number, int Count)>();

        AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("partitioned", async ctx =>
                    {
                        seen.Add((ctx.ScenarioInfo.ThreadNumber, ctx.ScenarioInfo.CopyCount));
                        await Task.Delay(Time.Milliseconds(20));
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.IterationsForConstant(copies: 4, iterations: 40)))
            .WithoutReports()
            .Run();

        await Assert.That(seen).IsNotEmpty();
        await Assert.That(seen.Select(x => x.Count).Distinct()).IsEquivalentTo(new[] { 4 });
        await Assert.That(seen.Select(x => x.Number).Distinct().Count()).IsEqualTo(4);
    }

    [Test]
    public async Task Every_row_is_owned_by_exactly_one_copy()
    {
        const int copyCount = 7;
        const int rowCount = 100;

        var owners = new List<int>[rowCount];
        for (var i = 0; i < rowCount; i++) owners[i] = [];

        for (var copy = 0; copy < copyCount; copy++)
        {
            var context = new FakeScenarioContext(copy, copyCount);

            for (var row = 0; row < rowCount; row++)
                if (context.OwnsIndex(row)) owners[row].Add(copy);
        }

        foreach (var row in owners)
            await Assert.That(row.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Partition_hands_each_copy_its_own_stride()
    {
        var rows = Enumerable.Range(0, 20).ToArray();

        var first = new FakeScenarioContext(0, 4).Partition(rows).ToArray();
        var second = new FakeScenarioContext(1, 4).Partition(rows).ToArray();

        await Assert.That(first).IsEquivalentTo(new[] { 0, 4, 8, 12, 16 });
        await Assert.That(second).IsEquivalentTo(new[] { 1, 5, 9, 13, 17 });
    }

    [Test]
    public async Task A_single_copy_owns_everything()
    {
        var rows = Enumerable.Range(0, 5).ToArray();
        var context = new FakeScenarioContext(0, 1);

        await Assert.That(context.Partition(rows)).IsEquivalentTo(rows);

        foreach (var row in rows)
            await Assert.That(context.OwnsIndex(row)).IsTrue();
    }

    /// <summary>Just enough of the context to exercise the partitioning helpers.</summary>
    private sealed class FakeScenarioContext(int threadNumber, int copyCount) : IScenarioContext
    {
        public TestInfo TestInfo => TestInfo.Empty;

        public ScenarioInfo ScenarioInfo { get; } = new()
        {
            ThreadId = $"s_{threadNumber}",
            ThreadNumber = threadNumber,
            CopyCount = copyCount,
            ScenarioName = "s",
            ScenarioDuration = TimeSpan.Zero,
            ScenarioOperation = ScenarioOperation.Bombing
        };

        public HostInfo HostInfo => HostInfo.Empty;
        public Microsoft.Extensions.Logging.ILogger Logger =>
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public int InvocationNumber => 1;
        public Dictionary<string, object> Data { get; } = [];
        public CancellationToken CancellationToken => CancellationToken.None;
        public Autobahn.Metrics.IMetricRegistry Metrics { get; } =
            new Autobahn.Internal.Domain.Metrics.MetricRegistry();

        public void StopScenario(string scenarioName, string reason) { }
        public void StopCurrentTest(string reason) { }
    }
}

[NotInParallel]
public class ForcibleStopTests
{
    private static ScenarioProps LongRunning(string name, Action<IScenarioContext>? onIteration = null) =>
        Scenario.Create(name, async ctx =>
            {
                onIteration?.Invoke(ctx);
                await Task.Delay(Time.Milliseconds(50));
                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(copies: 2, during: Time.Minutes(5)));

    [Test]
    [Category("slow")]
    public async Task Cancelling_the_session_token_ends_the_run_and_keeps_what_it_measured()
    {
        using var cts = new CancellationTokenSource();
        var iterations = 0;

        var reportFolder = Path.Combine(Path.GetTempPath(), $"autobahn_cancel_{Guid.NewGuid():N}");

        var runTask = Task.Run(() => AutobahnRunner
            .RegisterScenarios(LongRunning("cancellable", _ =>
            {
                if (Interlocked.Increment(ref iterations) == 20) cts.Cancel();
            }))
            .WithReportFolder(reportFolder)
            .WithReportFormats(ReportFormat.Txt)
            .WithCancellationToken(cts.Token)
            .Run());

        var completed = await Task.WhenAny(runTask, Task.Delay(Time.Seconds(60)));
        await Assert.That(completed).IsSameReferenceAs(runTask);

        var stats = await runTask;

        // The plan asked for five minutes. Cancelling stopped it in seconds, and the numbers
        // it had collected up to that point are still here.
        await Assert.That(stats.Duration).IsLessThan(Time.Minutes(1));
        await Assert.That(stats.AllOkCount).IsGreaterThanOrEqualTo(20);
        await Assert.That(Directory.EnumerateFiles(reportFolder, "*.txt", SearchOption.AllDirectories)).IsNotEmpty();

        Directory.Delete(reportFolder, recursive: true);
    }

    [Test]
    [Category("slow")]
    public async Task A_token_that_is_already_cancelled_still_produces_a_session_result()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var stats = AutobahnRunner
            .RegisterScenarios(LongRunning("already_cancelled"))
            .WithoutReports()
            .WithCancellationToken(cts.Token)
            .Run();

        await Assert.That(stats.ScenarioStats).IsNotEmpty();
        await Assert.That(stats.Duration).IsLessThan(Time.Minutes(1));
    }
}

[NotInParallel]
public class IterationRestartTests
{
    private static SessionStats Run(bool restartOnFail, out int stepsAfterTheFailure)
    {
        var afterFailure = 0;

        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("restart", async ctx =>
                    {
                        await Step.Run("first", ctx, () => Task.FromResult(Response.Ok()));
                        await Step.Run("failing", ctx, () => Task.FromResult(Response.Fail()));

                        return await Step.Run("after", ctx, () =>
                        {
                            Interlocked.Increment(ref afterFailure);
                            return Task.FromResult(Response.Ok());
                        });
                    })
                    .WithoutWarmUp()
                    .WithRestartIterationOnFail(restartOnFail)
                    .WithLoadSimulations(Simulation.IterationsForConstant(copies: 1, iterations: 20)))
                .WithoutReports()
                .Run();

        stepsAfterTheFailure = afterFailure;
        return stats;
    }

    [Test]
    public async Task A_failed_step_abandons_the_rest_of_the_iteration_by_default()
    {
        var stats = Run(restartOnFail: true, out var afterFailure);
        var scnStats = stats.GetScenarioStats("restart");

        await Assert.That(afterFailure).IsEqualTo(0);
        await Assert.That(scnStats.StepStats.Select(x => x.StepName)).DoesNotContain("after");

        // The iteration itself is counted as failed, but with no status code of its own:
        // the step that actually failed already recorded one.
        await Assert.That(scnStats.Fail.Request.Count).IsEqualTo(20);
        await Assert.That(scnStats.Ok.Request.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Turning_restart_off_lets_the_iteration_carry_on_past_a_failed_step()
    {
        var stats = Run(restartOnFail: false, out var afterFailure);
        var scnStats = stats.GetScenarioStats("restart");

        await Assert.That(afterFailure).IsEqualTo(20);
        await Assert.That(scnStats.GetStepStats("after").Ok.Request.Count).IsEqualTo(20);
        await Assert.That(scnStats.GetStepStats("failing").Fail.Request.Count).IsEqualTo(20);
        await Assert.That(scnStats.Ok.Request.Count).IsEqualTo(20);
    }
}
