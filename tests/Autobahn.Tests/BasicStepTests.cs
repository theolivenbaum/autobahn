namespace Autobahn.Tests;

[NotInParallel]
public class BasicStepTests
{
    [Test]
    public async Task Ok_and_fail_responses_land_on_the_right_side_of_the_step()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("count test", async ctx =>
                    {
                        await Step.Run("ok step", ctx, async () =>
                        {
                            await Task.Delay(Time.Milliseconds(100));
                            return Response.Ok();
                        });

                        await Step.Run("fail step", ctx, async () =>
                        {
                            await Task.Delay(Time.Milliseconds(100));
                            return Response.Fail();
                        });

                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.Inject(rate: 1, interval: Time.Seconds(1), during: Time.Seconds(2))))
            .WithoutReports()
            .Run();

        var okStep = stats.ScenarioStats[0].GetStepStats("ok step");
        var failStep = stats.ScenarioStats[0].GetStepStats("fail step");

        await Assert.That(okStep.Ok.Request.Count).IsEqualTo(2);
        await Assert.That(okStep.Fail.Request.Count).IsEqualTo(0);
        await Assert.That(failStep.Ok.Request.Count).IsEqualTo(0);
        await Assert.That(failStep.Fail.Request.Count).IsEqualTo(2);

        // The iteration restarted on the failed step, so the scenario itself is a failure.
        await Assert.That(stats.ScenarioStats[0].Ok.Request.Count).IsEqualTo(0);
        await Assert.That(stats.ScenarioStats[0].Fail.Request.Count).IsEqualTo(2);
    }

    [Test]
    [Category("slow")]
    public async Task Latency_throughput_and_data_transfer_land_in_the_expected_range()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("latency count test", async _ =>
                    {
                        await Task.Delay(Time.Milliseconds(100));
                        return Response.Ok(sizeBytes: 1000);
                    })
                    .WithWarmUpDuration(Time.Seconds(1))
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(10))))
            .WithoutReports()
            .Run();

        var ok = stats.ScenarioStats[0].Ok;

        await Assert.That(ok.Request.RPS).IsGreaterThanOrEqualTo(7.0);
        await Assert.That(ok.Request.RPS).IsLessThanOrEqualTo(10.0);
        await Assert.That(ok.Latency.MinMs).IsGreaterThanOrEqualTo(90.0);
        await Assert.That(ok.DataTransfer.MinBytes).IsEqualTo(1000L);
        await Assert.That(ok.DataTransfer.AllBytes).IsGreaterThanOrEqualTo(70_000L);
    }

    [Test]
    public async Task Failures_during_warm_up_do_not_end_the_run()
    {
        var result = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("scenario", async _ =>
                    {
                        await Task.Delay(Time.Milliseconds(100));
                        return Response.Fail();
                    })
                    .WithWarmUpDuration(Time.Seconds(5))
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(10))))
            .WithoutReports()
            .RunWithResult();

        await Assert.That(result.FinalStats.ScenarioStats).IsNotEmpty();
    }

    [Test]
    public async Task A_client_measured_latency_is_reported_instead_of_the_wall_clock_one()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("scenario", async _ =>
                    {
                        await Task.Delay(Time.Milliseconds(100));
                        return Response.Ok(latencyMs: 2_000.0);
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(3))))
            .WithoutReports()
            .Run();

        var scnStats = stats.ScenarioStats[0];

        await Assert.That(scnStats.Ok.Request.Count).IsGreaterThan(5);
        await Assert.That(scnStats.Ok.Latency.MinMs).IsGreaterThanOrEqualTo(1_900.0);
    }

    [Test]
    public async Task StopCurrentTest_ends_every_scenario()
    {
        var counter = 0;
        var duration = Time.Seconds(42);

        var scenario1 = Scenario.Create("test_youtube_1", async ctx =>
            {
                await Task.Delay(Time.Milliseconds(100));
                if (Interlocked.Increment(ref counter) >= 30) ctx.StopCurrentTest("custom reason");
                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(10, duration));

        var scenario2 = Scenario.Create("test_youtube_2", async ctx =>
            {
                await Task.Delay(Time.Milliseconds(100));
                if (Interlocked.Increment(ref counter) >= 30) ctx.StopCurrentTest("custom reason");
                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(10, duration));

        var stats = AutobahnRunner.RegisterScenarios(scenario1, scenario2).WithoutReports().Run();

        await Assert.That(stats.GetScenarioStats("test_youtube_1").Duration).IsLessThan(duration);
    }

    [Test]
    [Category("slow")]
    public async Task The_invocation_number_restarts_after_warm_up()
    {
        var counter = 0;

        AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("scenario", async ctx =>
                    {
                        await Task.Delay(Time.Seconds(1));
                        counter = ctx.InvocationNumber;
                        return Response.Ok();
                    })
                    .WithWarmUpDuration(Time.Seconds(10))
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(10))))
            .WithoutReports()
            .Run();

        await Assert.That(counter).IsGreaterThanOrEqualTo(5);
        await Assert.That(counter).IsLessThanOrEqualTo(11);
    }

    [Test]
    public async Task A_failed_step_restarts_the_iteration_by_default()
    {
        var step3Invoked = false;

        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("scenario", async ctx =>
                    {
                        await Step.Run("step1", ctx, () => Task.FromResult(Response.Ok()));
                        await Step.Run("step2", ctx, () => Task.FromResult(Response.Fail()));

                        await Step.Run("step3", ctx, () =>
                        {
                            step3Invoked = true;
                            return Task.FromResult(Response.Ok());
                        });

                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(1))))
            .WithoutReports()
            .Run();

        var scnStats = stats.ScenarioStats[0];

        await Assert.That(step3Invoked).IsFalse();
        await Assert.That(scnStats.Ok.Request.Count).IsEqualTo(0);
        await Assert.That(scnStats.Fail.Request.Count).IsGreaterThan(0);
        await Assert.That(scnStats.Fail.Request.Count).IsEqualTo(scnStats.GetStepStats("step2").Fail.Request.Count);
        await Assert.That(scnStats.GetStepStats("step1").Ok.Request.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task WithRestartIterationOnFail_false_lets_the_iteration_continue()
    {
        var step3Invoked = false;

        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("scenario", async ctx =>
                    {
                        await Step.Run("step1", ctx, () => Task.FromResult(Response.Ok()));
                        await Step.Run("step2", ctx, () => Task.FromResult(Response.Fail()));

                        await Step.Run("step3", ctx, () =>
                        {
                            step3Invoked = true;
                            return Task.FromResult(Response.Ok());
                        });

                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithRestartIterationOnFail(false)
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(1))))
            .WithoutReports()
            .Run();

        var scnStats = stats.ScenarioStats[0];

        await Assert.That(step3Invoked).IsTrue();
        await Assert.That(scnStats.Ok.Request.Count).IsGreaterThan(0);
        await Assert.That(scnStats.Fail.Request.Count).IsEqualTo(0);
        await Assert.That(scnStats.GetStepStats("step1").Ok.Request.Count).IsGreaterThan(0);
        await Assert.That(scnStats.GetStepStats("step2").Fail.Request.Count).IsGreaterThan(0);
        await Assert.That(scnStats.GetStepStats("step3").Ok.Request.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task A_cancelled_operation_is_recorded_as_a_timeout()
    {
        var scn1 = Scenario.Create("scenario_1", async ctx =>
            {
                await Step.Run("step1", ctx, async () =>
                {
                    using var timeout = new CancellationTokenSource();
                    timeout.CancelAfter(50);

                    await Task.Delay(100, timeout.Token);

                    return Response.Ok();
                });

                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(1)));

        var scn2 = Scenario.Create("scenario_2", async _ =>
            {
                using var timeout = new CancellationTokenSource();
                timeout.CancelAfter(50);

                await Task.Delay(100, timeout.Token);

                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(1)));

        var stats = AutobahnRunner.RegisterScenarios(scn1, scn2).WithoutReports().Run();

        foreach (var name in new[] { "scenario_1", "scenario_2" })
        {
            var scnStats = stats.GetScenarioStats(name);

            await Assert.That(scnStats.Fail.StatusCodes[0].IsError).IsTrue();
            await Assert.That(scnStats.Fail.StatusCodes[0].StatusCode).IsEqualTo(Constants.TimeoutStatusCode);
            await Assert.That(scnStats.Fail.StatusCodes[0].Count).IsEqualTo(scnStats.Fail.Request.Count);
        }
    }

    [Test]
    public async Task An_unhandled_exception_gets_its_own_status_code_and_message()
    {
        var scn1 = Scenario.Create("scenario_1", async ctx =>
            {
                await Step.Run<object>("step1", ctx, async () =>
                {
                    await Task.Delay(100);
                    throw new InvalidOperationException("my exception");
                });

                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(1)));

        var scn2 = Scenario.Create("scenario_2", async _ =>
            {
                await Task.Delay(100);
                throw new InvalidOperationException("my exception");
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(1)));

        var stats = AutobahnRunner.RegisterScenarios(scn1, scn2).WithoutReports().Run();

        foreach (var name in new[] { "scenario_1", "scenario_2" })
        {
            var scnStats = stats.GetScenarioStats(name);

            await Assert.That(scnStats.Fail.StatusCodes[0].IsError).IsTrue();
            await Assert.That(scnStats.Fail.StatusCodes[0].StatusCode).IsEqualTo(Constants.UnhandledExceptionCode);
            await Assert.That(scnStats.Fail.StatusCodes[0].Count).IsEqualTo(scnStats.Fail.Request.Count);
            await Assert.That(scnStats.Fail.StatusCodes[0].Message).IsEqualTo("my exception");
        }
    }

    [Test]
    public async Task A_response_message_travels_with_its_status_code()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("scenario", async ctx =>
                    {
                        await Step.Run("step", ctx, async () =>
                        {
                            await Task.Delay(100);
                            return Response.Ok(statusCode: "200", message: "my message 1");
                        });

                        return Response.Ok(statusCode: "300", message: "my message 2");
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(1))))
            .WithoutReports()
            .Run();

        var scnStats = stats.GetScenarioStats("scenario");

        await Assert.That(scnStats.Ok.StatusCodes[0].IsError).IsFalse();
        await Assert.That(scnStats.Ok.StatusCodes[0].StatusCode).IsEqualTo("200");
        await Assert.That(scnStats.Ok.StatusCodes[0].Count).IsEqualTo(scnStats.StepStats[0].Ok.Request.Count);
        await Assert.That(scnStats.Ok.StatusCodes[0].Message).IsEqualTo("my message 1");

        await Assert.That(scnStats.Ok.StatusCodes[1].StatusCode).IsEqualTo("300");
        await Assert.That(scnStats.Ok.StatusCodes[1].Count).IsEqualTo(scnStats.Ok.Request.Count);
        await Assert.That(scnStats.Ok.StatusCodes[1].Message).IsEqualTo("my message 2");
    }

    [Test]
    public async Task The_reserved_step_name_stops_the_test_rather_than_corrupting_the_stats()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("scenario", async ctx =>
                    {
                        await Step.Run(Constants.ScenarioGlobalInfo, ctx, () => Task.FromResult(Response.Ok()));
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(10))))
            .WithoutReports()
            .Run();

        await Assert.That(stats.ScenarioStats[0].Duration).IsLessThan(Time.Seconds(10));
    }
}
