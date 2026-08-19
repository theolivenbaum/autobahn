using Autobahn.Internal;
using Microsoft.Extensions.Logging;

namespace Autobahn.Tests;

[NotInParallel]
public class WarmUpTests
{
    [Test]
    public async Task Warm_up_measurements_stay_out_of_the_final_stats()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("warmup test", async ctx =>
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
                    .WithWarmUpDuration(Time.Seconds(1))
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 1, during: Time.Seconds(1))))
            .WithoutReports()
            .Run();

        var okStep = stats.ScenarioStats[0].GetStepStats("ok step");
        var failStep = stats.ScenarioStats[0].GetStepStats("fail step");

        await Assert.That(okStep.Ok.Request.Count).IsLessThanOrEqualTo(10);
        await Assert.That(okStep.Fail.Request.Count).IsEqualTo(0);
        await Assert.That(failStep.Ok.Request.Count).IsEqualTo(0);
        await Assert.That(failStep.Fail.Request.Count).IsLessThanOrEqualTo(10);
    }

    [Test]
    public async Task WithoutWarmUp_skips_the_warm_up_phase_entirely()
    {
        var warmUpRan = false;
        var logs = new InMemoryLoggerProvider();

        AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("1", async ctx =>
                    {
                        if (ctx.ScenarioInfo.ScenarioOperation == ScenarioOperation.WarmUp) warmUpRan = true;

                        await Task.Delay(Time.Seconds(0.5));
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(1))))
            .WithLogging(builder => builder.AddProvider(logs))
            .WithoutReports()
            .Run("disposeLogger=false");

        await Assert.That(warmUpRan).IsFalse();
        await Assert.That(logs.HasMessageContaining("Starting warm up...")).IsFalse();
        await Assert.That(logs.HasMessageContaining("Starting bombing...")).IsTrue();
    }

    [Test]
    public async Task Warm_up_runs_only_for_the_scenarios_that_asked_for_it()
    {
        var warmUp1 = false;
        var warmUp2 = false;
        var bombing1 = false;
        var bombing2 = false;

        var scn1 = Scenario.Create("1", async ctx =>
            {
                if (ctx.ScenarioInfo.ScenarioOperation == ScenarioOperation.WarmUp) warmUp1 = true;
                if (ctx.ScenarioInfo.ScenarioOperation == ScenarioOperation.Bombing) bombing1 = true;

                await Task.Delay(Time.Seconds(0.5));
                return Response.Ok();
            })
            .WithWarmUpDuration(Time.Seconds(2))
            .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(2)));

        var scn2 = Scenario.Create("2", async ctx =>
            {
                if (ctx.ScenarioInfo.ScenarioOperation == ScenarioOperation.WarmUp) warmUp2 = true;
                if (ctx.ScenarioInfo.ScenarioOperation == ScenarioOperation.Bombing) bombing2 = true;

                await Task.Delay(Time.Seconds(0.5));
                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(2)));

        AutobahnRunner.RegisterScenarios(scn1, scn2).WithoutReports().Run();

        await Assert.That(warmUp1).IsTrue();
        await Assert.That(warmUp2).IsFalse();
        await Assert.That(bombing1).IsTrue();
        await Assert.That(bombing2).IsTrue();
    }

    [Test]
    public async Task A_warm_up_longer_than_the_scenario_stops_the_run()
    {
        var context = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("1", async _ =>
                    {
                        await Task.Delay(Time.Seconds(0.5));
                        return Response.Ok();
                    })
                    .WithWarmUpDuration(Time.Seconds(5))
                    .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(2))))
            .WithoutReports();

        var error = Assert.Throws<AutobahnException>(() => context.RunWithResult());

        await Assert.That(error!.Error).IsTypeOf<ScenarioError.WarmUpDurationIsBiggerScnDuration>();
    }
}
