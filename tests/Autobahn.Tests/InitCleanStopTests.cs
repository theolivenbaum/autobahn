using Microsoft.Extensions.Configuration;

namespace Autobahn.Tests;

[NotInParallel]
public class InitCleanStopTests
{
    public sealed record TestCustomSettings
    {
        public string TargetHost { get; init; } = "";
        public int MsgSizeInBytes { get; init; }
        public int PauseMs { get; init; }
    }

    [Test]
    public async Task Clean_runs_once_and_a_throwing_clean_does_not_fail_the_run()
    {
        var cleanInvokeCounter = 0;

        AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("withTestClean test", async _ =>
                    {
                        await Task.Delay(Time.Milliseconds(100));
                        return Response.Ok();
                    })
                    .WithClean(_ =>
                    {
                        cleanInvokeCounter++;
                        throw new InvalidOperationException("exception was not handled");
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(2, Time.Seconds(1))))
            .WithoutReports()
            .Run();

        await Assert.That(cleanInvokeCounter).IsEqualTo(1);
    }

    [Test]
    public async Task Init_receives_the_custom_settings_from_the_json_config()
    {
        IScenarioInitContext? scnContext = null;

        AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("test_youtube", async _ =>
                    {
                        await Task.Delay(Time.Milliseconds(100));
                        return Response.Ok();
                    })
                    .WithInit(ctx =>
                    {
                        scnContext = ctx;
                        return Task.CompletedTask;
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(2, Time.Seconds(2))))
            .LoadConfig("Assets/Configuration/test_config.json")
            .WithoutReports()
            .Run();

        var customSettings = scnContext!.CustomSettings.Get<TestCustomSettings>()!;

        await Assert.That(customSettings.TargetHost).IsEqualTo("localhost");
        await Assert.That(customSettings.MsgSizeInBytes).IsEqualTo(1000);
    }

    [Test]
    public async Task An_init_that_throws_stops_the_run()
    {
        var context = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("test_youtube", async _ =>
                    {
                        await Task.Delay(Time.Milliseconds(100));
                        return Response.Ok();
                    })
                    .WithInit(_ => throw new InvalidOperationException("my error"))
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(2, Time.Seconds(2))))
            .WithoutReports();

        var error = Assert.Throws<AutobahnException>(() => context.Run());

        await Assert.That(error!.Message).Contains("Init scenario error");
    }

    [Test]
    public async Task StopScenario_ends_one_scenario_and_leaves_the_others_running()
    {
        var counter = 0;
        var duration = Time.Seconds(15);

        var scenario1 = Scenario.Create("test_youtube_1", async ctx =>
            {
                await Task.Delay(Time.Milliseconds(100));

                if (Interlocked.Increment(ref counter) == 30)
                    ctx.StopScenario("test_youtube_1", "custom reason");

                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(10, duration));

        var scenario2 = Scenario.Create("test_youtube_2", async _ =>
            {
                await Task.Delay(Time.Milliseconds(100));
                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(10, duration));

        var stats = AutobahnRunner.RegisterScenarios(scenario1, scenario2).WithoutReports().Run();

        await Assert.That(stats.GetScenarioStats("test_youtube_1").Duration).IsLessThan(duration);
        await Assert.That(stats.GetScenarioStats("test_youtube_2").Duration).IsEqualTo(duration);
    }

    [Test]
    public async Task The_run_ends_once_every_scenario_has_stopped_itself()
    {
        var counter1 = 0;
        var counter2 = 0;
        var duration = Time.Seconds(60);

        var scenario1 = Scenario.Create("test_youtube_1", async ctx =>
            {
                await Task.Delay(Time.Milliseconds(100));
                if (Interlocked.Increment(ref counter1) == 30) ctx.StopScenario("test_youtube_1", "custom reason");
                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(1, duration));

        var scenario2 = Scenario.Create("test_youtube_2", async ctx =>
            {
                await Task.Delay(Time.Milliseconds(100));
                if (Interlocked.Increment(ref counter2) == 60) ctx.StopScenario("test_youtube_2", "custom reason");
                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(1, duration));

        var stats = AutobahnRunner.RegisterScenarios(scenario1, scenario2).WithoutReports().Run();

        await Assert.That(stats.GetScenarioStats("test_youtube_1").Duration).IsLessThan(duration);
        await Assert.That(stats.GetScenarioStats("test_youtube_2").Duration).IsLessThan(duration);
    }

    [Test]
    public async Task Clean_sees_the_duration_the_scenario_actually_ran_for()
    {
        var duration = Time.Seconds(60);
        var plannedDuration = TimeSpan.Zero;
        var executionDuration = TimeSpan.Zero;

        var scenario = Scenario.Create("test_youtube_1", async ctx =>
            {
                await Task.Delay(Time.Seconds(1));

                if (ctx.InvocationNumber > 2) ctx.StopCurrentTest("no reason");

                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithInit(ctx =>
            {
                plannedDuration = ctx.ScenarioInfo.ScenarioDuration;
                return Task.CompletedTask;
            })
            .WithClean(ctx =>
            {
                executionDuration = ctx.ScenarioInfo.ScenarioDuration;
                return Task.CompletedTask;
            })
            .WithLoadSimulations(Simulation.KeepConstant(1, duration));

        var stats = AutobahnRunner.RegisterScenarios(scenario).WithoutReports().Run();

        await Assert.That(plannedDuration).IsEqualTo(duration);
        await Assert.That(stats.ScenarioStats[0].Duration.Seconds).IsEqualTo(executionDuration.Seconds);
        await Assert.That(stats.ScenarioStats[0].Duration).IsLessThan(duration);
    }

    [Test]
    [Category("slow")]
    public async Task Too_many_failed_iterations_end_the_run()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("test_youtube_1", async _ =>
                    {
                        await Task.Delay(Time.Milliseconds(100));
                        return Response.Fail();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(100, Time.Seconds(60))))
            .WithoutReports()
            .Run();

        await Assert.That(stats.ScenarioStats[0].Fail.Request.Count)
            .IsGreaterThanOrEqualTo(Constants.ScenarioMaxFailCount);
    }

    [Test]
    public async Task WithMaxFailCount_lowers_the_bar_for_ending_the_run()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("test_youtube_1", async _ =>
                    {
                        await Task.Delay(Time.Milliseconds(500));
                        return Response.Fail();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.Inject(1, Time.Seconds(1), Time.Seconds(60)))
                    .WithMaxFailCount(1))
            .WithoutReports()
            .Run();

        await Assert.That(stats.ScenarioStats[0].Fail.Request.Count).IsLessThanOrEqualTo(2);
    }

    [Test]
    public async Task A_failed_step_is_not_a_failed_iteration()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("test_youtube_1", async ctx =>
                    {
                        await Step.Run("fail_step", ctx, async () =>
                        {
                            await Task.Delay(Time.Milliseconds(10));
                            return Response.Fail();
                        });

                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithRestartIterationOnFail(false)
                    .WithLoadSimulations(Simulation.KeepConstant(10, Time.Seconds(2)))
                    .WithMaxFailCount(1))
            .WithoutReports()
            .Run();

        await Assert.That(stats.ScenarioStats[0].Fail.Request.Count).IsEqualTo(0);
    }
}
