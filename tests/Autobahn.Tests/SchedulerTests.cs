using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Scheduler;

namespace Autobahn.Tests;

/// <summary>The pure scheduling decisions, with no actors involved.</summary>
internal class ScenarioSchedulerDecisionTests
{
    private static SimulationPlanItem Item(LoadSimulation value, int prevActorCount) => new()
    {
        Value = value,
        StartTime = TimeSpan.Zero,
        EndTime = TimeSpan.Zero,
        Duration = TimeSpan.Zero,
        PrevActorCount = prevActorCount
    };

    private static int FixedRandom(int minRate, int maxRate) => 42;

    [Test]
    [Arguments(100, 200, 50, 150)]    // ramp-up
    [Arguments(0, 100, 99, 99)]       // ramp-up
    [Arguments(70, 100, 1, 70)]       // ramp-up
    [Arguments(70, 100, 100, 100)]    // ramp-up
    [Arguments(300, 100, 50, 200)]    // ramp-down
    [Arguments(1_000, 100, 99, 109)]  // ramp-down
    [Arguments(1_000, 100, 100, 100)] // ramp-down
    [Arguments(1_000, 100, 2, 982)]   // ramp-down
    [Arguments(1_000, 0, 100, 0)]     // ramp-down
    public async Task RampingInject_ramps_the_injection_rate(
        int prevCopiesCount, int rate, int timeProgress, int expected)
    {
        var simulation = Item(Simulation.RampingInject(rate, TimeSpan.Zero, TimeSpan.Zero), prevCopiesCount);

        var (command, copiesCount) = ScenarioScheduler.Schedule(
            FixedRandom, simulation, timeProgress, currentConstActorCount: 0);

        await Assert.That(command).IsEqualTo(SchedulerCommand.InjectOneTimeActors);
        await Assert.That(copiesCount).IsEqualTo(expected);
    }

    [Test]
    [Arguments(100, 200, 50, 200)]
    [Arguments(0, 100, 99, 100)]
    [Arguments(70, 100, 1, 100)]
    [Arguments(70, 0, 100, 0)]
    public async Task Inject_holds_the_configured_rate(
        int prevCopiesCount, int rate, int timeProgress, int expected)
    {
        var simulation = Item(Simulation.Inject(rate, TimeSpan.Zero, TimeSpan.Zero), prevCopiesCount);

        var (command, copiesCount) = ScenarioScheduler.Schedule(
            FixedRandom, simulation, timeProgress, currentConstActorCount: 0);

        await Assert.That(command).IsEqualTo(SchedulerCommand.InjectOneTimeActors);
        await Assert.That(copiesCount).IsEqualTo(expected);
    }

    [Test]
    public async Task InjectRandom_asks_for_a_rate_between_its_bounds()
    {
        var simulation = Item(Simulation.InjectRandom(1, 100, TimeSpan.Zero, TimeSpan.Zero), 0);

        var (command, copiesCount) = ScenarioScheduler.Schedule(
            FixedRandom, simulation, timeProgress: 50, currentConstActorCount: 0);

        await Assert.That(command).IsEqualTo(SchedulerCommand.InjectOneTimeActors);
        await Assert.That(copiesCount).IsEqualTo(42);
    }

    [Test]
    [Arguments(0, 20, 0, 3, 1)]
    [Arguments(0, 100, 50, 51, 1)]
    [Arguments(100, 200, 130, 31, 1)]
    [Arguments(0, 100, 90, 95, 5)]
    public async Task RampingConstant_adds_actors_while_ramping_up(
        int prevCopiesCount, int copiesCount, int currentConstActorCount, int timeProgress, int expected)
    {
        var simulation = Item(Simulation.RampingConstant(copiesCount, TimeSpan.Zero), prevCopiesCount);

        var (command, count) = ScenarioScheduler.Schedule(
            FixedRandom, simulation, timeProgress, currentConstActorCount);

        await Assert.That(command).IsEqualTo(SchedulerCommand.AddConstantActors);
        await Assert.That(count).IsEqualTo(expected);
    }

    [Test]
    [Arguments(100, 0, 50, 51, 1)]
    [Arguments(200, 100, 170, 31, 1)]
    [Arguments(100, 0, 10, 95, 5)]
    public async Task RampingConstant_removes_actors_while_ramping_down(
        int prevCopiesCount, int copiesCount, int currentConstActorCount, int timeProgress, int expected)
    {
        var simulation = Item(Simulation.RampingConstant(copiesCount, TimeSpan.Zero), prevCopiesCount);

        var (command, count) = ScenarioScheduler.Schedule(
            FixedRandom, simulation, timeProgress, currentConstActorCount);

        await Assert.That(command).IsEqualTo(SchedulerCommand.RemoveConstantActors);
        await Assert.That(count).IsEqualTo(expected);
    }

    [Test]
    [Arguments(20, 0, 20, SchedulerCommand.AddConstantActors)]
    [Arguments(20, 20, 0, SchedulerCommand.DoNothing)]
    [Arguments(10, 20, 10, SchedulerCommand.RemoveConstantActors)]
    public async Task KeepConstant_moves_the_actor_count_towards_its_target(
        int copiesCount, int currentConstActorCount, int expected, SchedulerCommand expectedCommand)
    {
        var simulation = Item(Simulation.KeepConstant(copiesCount, TimeSpan.Zero), prevActorCount: 0);

        var (command, count) = ScenarioScheduler.Schedule(
            FixedRandom, simulation, timeProgress: 1, currentConstActorCount);

        await Assert.That(command).IsEqualTo(expectedCommand);
        await Assert.That(count).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0, SchedulerCommand.DoNothing, 0)]
    [Arguments(1, SchedulerCommand.RemoveConstantActors, 1)]
    [Arguments(10, SchedulerCommand.RemoveConstantActors, 10)]
    public async Task Pause_stops_whatever_is_still_running(
        int currentConstActorCount, SchedulerCommand expectedCommand, int expected)
    {
        var simulation = Item(Simulation.Pause(Time.Seconds(1)), prevActorCount: 20);

        var (command, count) = ScenarioScheduler.Schedule(
            FixedRandom, simulation, timeProgress: 50, currentConstActorCount);

        await Assert.That(command).IsEqualTo(expectedCommand);
        await Assert.That(count).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0, SchedulerCommand.DoNothing, 0)]
    [Arguments(1, SchedulerCommand.RemoveConstantActors, 1)]
    [Arguments(10, SchedulerCommand.RemoveConstantActors, 10)]
    public async Task Switching_from_a_closed_to_an_open_model_clears_the_leftover_actors(
        int currentConstActorCount, SchedulerCommand expectedCommand, int expected)
    {
        var simulation = Item(Simulation.Pause(Time.Seconds(1)), prevActorCount: 20);

        var (command, count) = ScenarioScheduler.ScheduleCleanPrevSimulation(simulation, currentConstActorCount);

        await Assert.That(command).IsEqualTo(expectedCommand);
        await Assert.That(count).IsEqualTo(expected);
    }

    [Test]
    public async Task Switching_between_two_closed_model_simulations_keeps_the_actors()
    {
        var simulation = Item(Simulation.KeepConstant(10, Time.Seconds(1)), prevActorCount: 20);

        var (command, count) = ScenarioScheduler.ScheduleCleanPrevSimulation(simulation, currentConstActorCount: 10);

        await Assert.That(command).IsEqualTo(SchedulerCommand.DoNothing);
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    [Arguments(0, 10, 5, 5)]
    [Arguments(10, 20, 9, 1)]
    [Arguments(0, 10, 10, 0)]
    [Arguments(0, 10, 12, 0)]
    public async Task CalcTimeDrift_reports_how_much_an_interval_overran(
        int startInterval, int endInterval, int simulationInterval, int expectedDrift)
    {
        var result = ScenarioScheduler.CalcTimeDrift(
            TimeSpan.FromSeconds(startInterval),
            TimeSpan.FromSeconds(endInterval),
            TimeSpan.FromSeconds(simulationInterval));

        await Assert.That(result).IsEqualTo(TimeSpan.FromSeconds(expectedDrift));
    }

    [Test]
    [Arguments(0, 0, 0)]
    [Arguments(5, 3, 2)]
    [Arguments(3, 5, 0)]
    public async Task Removing_more_actors_than_are_scheduled_floors_at_zero(
        int scheduled, int removeCount, int expected)
    {
        await Assert.That(ConstantActorScheduler.RemoveFromScheduler(scheduled, removeCount)).IsEqualTo(expected);
    }
}
