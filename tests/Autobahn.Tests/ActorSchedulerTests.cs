using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Concurrency;
using Autobahn.Internal.Domain.Metrics;
using Autobahn.Internal.Domain.Scheduler;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Stats;
using Microsoft.Extensions.Logging.Abstractions;

namespace Autobahn.Tests;

/// <summary>Builds the shared context one scenario's actors run against.</summary>
internal static class SchedulerTestContext
{
    public static RuntimeScenario BaseScenario() =>
        ScenarioFactory.CreateScenarios(
        [
            Scenario.Create("test_scn", async ctx =>
                {
                    await Task.Delay(Time.Milliseconds(100));
                    return Response.Ok();
                })
                .WithLoadSimulations(Simulation.KeepConstant(100, Time.Seconds(30)))
        ]).Value[0];

    public static ScenarioContextArgs Create(out ScenarioStatsActor statsActor)
    {
        var scenario = BaseScenario();
        statsActor = new ScenarioStatsActor(NullLogger.Instance, scenario, Constants.DefaultReportingInterval);

        return new ScenarioContextArgs
        {
            Logger = NullLogger.Instance,
            Scenario = scenario,
            ScenarioCancellationToken = new CancellationTokenSource(),
            ScenarioOperation = ScenarioOperation.Bombing,
            ScenarioStatsActor = statsActor,
            ExecStopCommand = _ => { },
            TestInfo = TestInfo.Empty,
            GetHostInfo = () => HostInfo.Empty,
            Metrics = new MetricRegistry()
        };
    }
}

[NotInParallel]
public class ConstantActorSchedulerTests
{
    [Test]
    public async Task AddActors_starts_actors_when_there_are_none()
    {
        var args = SchedulerTestContext.Create(out var statsActor);
        await using var _ = statsActor;
        using var scheduler = new ConstantActorScheduler(args);

        var initCount = scheduler.ScheduledActorCount;
        scheduler.AddActors(5, TimeSpan.Zero);
        await Task.Delay(Time.Milliseconds(200));

        await Assert.That(initCount).IsEqualTo(0);
        await Assert.That(scheduler.ScheduledActorCount).IsEqualTo(5);
        await Assert.That(ScenarioActorPool.GetWorkingActors(scheduler.AvailableActors).Count()).IsEqualTo(5);
    }

    [Test]
    public async Task Added_actors_keep_running_until_the_scenario_ends()
    {
        var args = SchedulerTestContext.Create(out var statsActor);
        await using var _ = statsActor;
        using var scheduler = new ConstantActorScheduler(args);

        scheduler.AddActors(5, TimeSpan.Zero);
        await Task.Delay(Time.Milliseconds(200));

        await Assert.That(scheduler.ScheduledActorCount).IsEqualTo(5);
        await Assert.That(ScenarioActorPool.GetWorkingActors(scheduler.AvailableActors).Count()).IsEqualTo(5);
        await Assert.That(scheduler.AvailableActors.Count).IsEqualTo(5);
    }

    [Test]
    public async Task RemoveActors_stops_some_actors_but_keeps_them_in_the_pool()
    {
        var args = SchedulerTestContext.Create(out var statsActor);
        await using var _ = statsActor;
        using var scheduler = new ConstantActorScheduler(args);

        scheduler.AddActors(10, TimeSpan.Zero);
        await Task.Delay(Time.Seconds(2));
        scheduler.RemoveActors(5);
        await Task.Delay(Time.Seconds(2));

        await Assert.That(scheduler.ScheduledActorCount).IsEqualTo(5);
        await Assert.That(ScenarioActorPool.GetWorkingActors(scheduler.AvailableActors).Count()).IsEqualTo(5);
        await Assert.That(scheduler.AvailableActors.Count).IsEqualTo(10);
    }

    [Test]
    public async Task AskToStop_stops_every_working_actor()
    {
        var args = SchedulerTestContext.Create(out var statsActor);
        await using var _ = statsActor;
        using var scheduler = new ConstantActorScheduler(args);

        scheduler.AddActors(5, TimeSpan.Zero);
        await Task.Delay(Time.Seconds(2));
        scheduler.AskToStop();
        await Task.Delay(Time.Seconds(2));

        await Assert.That(scheduler.ScheduledActorCount).IsEqualTo(5);
        await Assert.That(ScenarioActorPool.GetWorkingActors(scheduler.AvailableActors).Count()).IsEqualTo(0);
        await Assert.That(scheduler.AvailableActors.Count).IsEqualTo(5);
    }
}

[NotInParallel]
public class OneTimeActorSchedulerTests
{
    [Test]
    public async Task InjectActors_starts_actors_when_there_are_none()
    {
        var args = SchedulerTestContext.Create(out var statsActor);
        await using var _ = statsActor;
        using var scheduler = new OneTimeActorScheduler(args);

        var initCount = scheduler.ScheduledActorCount;
        scheduler.InjectActors(5, TimeSpan.Zero);
        await Task.Delay(Time.Milliseconds(50));

        await Assert.That(initCount).IsEqualTo(0);
        await Assert.That(scheduler.ScheduledActorCount).IsEqualTo(5);
        await Assert.That(ScenarioActorPool.GetWorkingActors(scheduler.AvailableActors).Count()).IsEqualTo(5);
    }

    [Test]
    public async Task Injected_actors_run_one_iteration_and_go_idle()
    {
        var args = SchedulerTestContext.Create(out var statsActor);
        await using var _ = statsActor;
        using var scheduler = new OneTimeActorScheduler(args);

        scheduler.InjectActors(5, TimeSpan.Zero);
        await Task.Delay(Time.Seconds(2));

        await Assert.That(scheduler.ScheduledActorCount).IsEqualTo(5);
        await Assert.That(ScenarioActorPool.GetWorkingActors(scheduler.AvailableActors).Count()).IsEqualTo(0);
    }

    [Test]
    public async Task AskToStop_stops_every_working_actor()
    {
        var args = SchedulerTestContext.Create(out var statsActor);
        await using var _ = statsActor;
        using var scheduler = new OneTimeActorScheduler(args);

        scheduler.InjectActors(5, TimeSpan.Zero);
        await Task.Delay(Time.Milliseconds(10));
        scheduler.AskToStop();
        await Task.Delay(Time.Seconds(2));

        await Assert.That(scheduler.ScheduledActorCount).IsEqualTo(5);
        await Assert.That(ScenarioActorPool.GetWorkingActors(scheduler.AvailableActors).Count()).IsEqualTo(0);
    }
}
