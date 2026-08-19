namespace Autobahn.Tests;

[NotInParallel]
public class RunnerTests
{
    [Test]
    public async Task WithTargetScenarios_runs_only_the_named_scenarios()
    {
        var scn1Started = false;
        var scn2Started = false;

        var scn1 = Scenario.Create("scn_1", async _ =>
            {
                await Task.Delay(Time.Milliseconds(100));
                return Response.Ok();
            })
            .WithInit(_ => { scn1Started = true; return Task.CompletedTask; })
            .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(1)))
            .WithoutWarmUp();

        var scn2 = Scenario.Create("scn_2", async _ =>
            {
                await Task.Delay(Time.Milliseconds(100));
                return Response.Ok();
            })
            .WithInit(_ => { scn2Started = true; return Task.CompletedTask; })
            .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(1)))
            .WithoutWarmUp();

        AutobahnRunner.RegisterScenarios(scn1, scn2)
            .WithTargetScenarios("scn_2")
            .WithoutReports()
            .Run();

        await Assert.That(scn1Started).IsFalse();
        await Assert.That(scn2Started).IsTrue();
    }

    [Test]
    public async Task Test_suite_and_name_reach_the_final_stats()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("scn", _ => Task.FromResult<IResponse>(Response.Ok()))
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(1))))
            .WithTestSuite("my suite")
            .WithTestName("my test")
            .WithoutReports()
            .Run();

        await Assert.That(stats.TestInfo.TestSuite).IsEqualTo("my suite");
        await Assert.That(stats.TestInfo.TestName).IsEqualTo("my test");
        await Assert.That(stats.TestInfo.SessionId).IsNotEmpty();
    }
}

[NotInParallel]
public class SessionStatsIntegrationTests
{
    [Test]
    [Category("slow")]
    public async Task Session_totals_add_up_across_scenarios_during_a_real_run()
    {
        var okScenario = Scenario.Create("ok scenario", async _ =>
            {
                await Task.Delay(Time.Milliseconds(500));
                return Response.Ok(sizeBytes: 100);
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.Inject(rate: 1, interval: Time.Seconds(0.5), during: Time.Seconds(10)));

        var failScenario = Scenario.Create("fail scenario", async _ =>
            {
                await Task.Delay(Time.Milliseconds(500));
                return Response.Fail(statusCode: "10", sizeBytes: 10, message: "reason");
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.Inject(rate: 1, interval: Time.Seconds(0.5), during: Time.Seconds(10)));

        var stats = AutobahnRunner.RegisterScenarios(okScenario, failScenario).WithoutReports().Run();

        var sc0 = stats.GetScenarioStats("ok scenario");
        var sc1 = stats.GetScenarioStats("fail scenario");

        await Assert.That(stats.Duration).IsEqualTo(Time.Seconds(10));
        await Assert.That(stats.AllRequestCount).IsEqualTo(40);
        await Assert.That(stats.AllOkCount).IsEqualTo(20);
        await Assert.That(stats.AllFailCount).IsEqualTo(20);
        await Assert.That(stats.AllBytes)
            .IsEqualTo(sc0.Ok.DataTransfer.AllBytes + sc1.Fail.DataTransfer.AllBytes);
    }

    [Test]
    public async Task Data_sizes_beyond_two_gigabytes_are_reported_intact()
    {
        const long sizeBytes = 3_000_000_000L;

        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("ok scenario", async _ =>
                    {
                        await Task.Delay(Time.Milliseconds(500));
                        return Response.Ok(sizeBytes: sizeBytes);
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.Inject(rate: 1, interval: Time.Seconds(0.5), during: Time.Seconds(2))))
            .WithoutReports()
            .Run();

        var sc = stats.GetScenarioStats("ok scenario");

        await Assert.That(stats.Duration).IsEqualTo(Time.Seconds(2));
        await Assert.That(stats.AllRequestCount).IsEqualTo(4);
        await Assert.That(stats.AllBytes).IsEqualTo(4L * sizeBytes);
        await Assert.That(sc.Ok.DataTransfer.MinBytes).IsEqualTo(sizeBytes);
        await Assert.That(sc.Ok.DataTransfer.MaxBytes).IsEqualTo(sizeBytes);
    }

    [Test]
    public async Task Per_step_statistics_are_measured_separately()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("realtime stats scenario", async ctx =>
                    {
                        await Step.Run("ok step", ctx, async () =>
                        {
                            await Task.Delay(Time.Milliseconds(500));
                            return Response.Ok(sizeBytes: 100);
                        });

                        await Step.Run("fail step", ctx, async () =>
                        {
                            await Task.Delay(Time.Milliseconds(500));
                            return Response.Fail(message: "reason 1", statusCode: "10", sizeBytes: 10);
                        });

                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.Inject(rate: 1, interval: Time.Seconds(1), during: Time.Seconds(5))))
            .WithoutReports()
            .Run();

        var scnStats = stats.ScenarioStats[0];
        var okStep = scnStats.GetStepStats("ok step");
        var failStep = scnStats.GetStepStats("fail step");

        await Assert.That(okStep.Ok.Request.Count).IsEqualTo(5);
        await Assert.That(okStep.Ok.Request.RPS).IsEqualTo(1.0);
        await Assert.That(okStep.Ok.DataTransfer.MinBytes).IsEqualTo(100L);
        await Assert.That(okStep.Fail.Request.Count).IsEqualTo(0);

        await Assert.That(failStep.Fail.Request.Count).IsEqualTo(5);
        await Assert.That(failStep.Fail.Request.RPS).IsEqualTo(1.0);
        await Assert.That(failStep.Fail.DataTransfer.MinBytes).IsEqualTo(10L);
        await Assert.That(failStep.Ok.Request.Count).IsEqualTo(0);
    }

    [Test]
    [Category("slow")]
    public async Task Status_codes_are_rolled_up_from_the_steps_to_the_scenario()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("realtime stats scenario", async ctx =>
                    {
                        await Step.Run("ok step", ctx, async () =>
                        {
                            await Task.Delay(Time.Milliseconds(100));
                            return Response.Ok(statusCode: "10");
                        });

                        await Step.Run("ok step no status", ctx, async () =>
                        {
                            await Task.Delay(Time.Milliseconds(100));
                            return Response.Ok();
                        });

                        await Step.Run("fail step", ctx, async () =>
                        {
                            await Task.Delay(Time.Milliseconds(100));
                            return Response.Fail(statusCode: "-10");
                        });

                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithRestartIterationOnFail(false)
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(10))))
            .WithoutReports()
            .Run();

        var scnStats = stats.ScenarioStats[0];
        var allCodes = scnStats.Ok.StatusCodes.Concat(scnStats.Fail.StatusCodes).ToArray();

        await Assert.That(allCodes.First(x => x.StatusCode is "10" or "-10").Count).IsGreaterThan(10);
        await Assert.That(scnStats.GetStepStats("ok step").Ok.StatusCodes.First(x => x.StatusCode == "10").Count)
            .IsGreaterThan(10);
        await Assert.That(scnStats.GetStepStats("fail step").Fail.StatusCodes.First(x => x.StatusCode == "-10").Count)
            .IsGreaterThan(10);
        await Assert.That(scnStats.GetStepStats("ok step no status").Ok.StatusCodes).IsEmpty();
    }

    [Test]
    [Category("slow")]
    public async Task Time_spent_paused_does_not_depress_the_throughput_it_reports()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("realtime stats scenario", async _ =>
                    {
                        await Task.Delay(Time.Milliseconds(100));
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(
                        Simulation.Pause(during: Time.Seconds(10)),
                        Simulation.Inject(rate: 100, interval: Time.Seconds(1), during: Time.Seconds(1))))
            .WithoutReports()
            .Run();

        var scnStats = stats.GetScenarioStats("realtime stats scenario");

        await Assert.That(scnStats.Ok.Request.Count).IsEqualTo(100);
        await Assert.That(scnStats.Ok.Request.RPS).IsEqualTo(100.0);
        await Assert.That(scnStats.Duration).IsEqualTo(Time.Seconds(11)); // inject (1s) + pause (10s)
    }
}
