using Autobahn.Internal;
using Autobahn.Internal.Domain;

namespace Autobahn.Tests;

public class LoadSimulationTests
{
    [Test]
    public async Task Create_orders_closed_model_simulations_along_the_timeline()
    {
        LoadSimulation[] simulations =
        [
            Simulation.RampingConstant(10, Time.Seconds(20)),
            Simulation.KeepConstant(20, Time.Seconds(50)),
            Simulation.KeepConstant(30, Time.Seconds(50)),
            Simulation.KeepConstant(1000, Time.Seconds(80)),
            Simulation.RampingConstant(0, Time.Seconds(20))
        ];

        var plan = SimulationPlan.Create(simulations);
        await Assert.That(plan.IsOk).IsTrue();

        var items = plan.Value;
        var planedDuration = SimulationPlan.GetPlanedDuration(items);

        await Assert.That(items.Count).IsEqualTo(simulations.Length);
        await Assert.That(items[0].StartTime <= items[^1].EndTime).IsTrue();
        await Assert.That(planedDuration).IsEqualTo(items[^1].EndTime);
        await Assert.That(items.SequenceEqual(items.OrderBy(x => x.EndTime))).IsTrue();
        await Assert.That(items[0].PrevActorCount).IsEqualTo(0);
        await Assert.That(items[^1].PrevActorCount).IsEqualTo(1000);
    }

    [Test]
    public async Task Create_orders_open_model_simulations_along_the_timeline()
    {
        LoadSimulation[] simulations =
        [
            Simulation.RampingInject(20, Time.Seconds(1), Time.Seconds(20)),
            Simulation.Inject(20, Time.Seconds(1), Time.Seconds(30)),
            Simulation.RampingInject(0, Time.Seconds(1), Time.Seconds(20))
        ];

        var plan = SimulationPlan.Create(simulations);
        await Assert.That(plan.IsOk).IsTrue();

        var items = plan.Value;
        var planedDuration = SimulationPlan.GetPlanedDuration(items);

        await Assert.That(items.Count).IsEqualTo(simulations.Length);
        await Assert.That(items[0].StartTime <= items[^1].EndTime).IsTrue();
        await Assert.That(planedDuration).IsEqualTo(items[^1].EndTime);
        await Assert.That(items.SequenceEqual(items.OrderBy(x => x.EndTime))).IsTrue();
        await Assert.That(items[0].PrevActorCount).IsEqualTo(0);
        await Assert.That(items[^1].PrevActorCount).IsEqualTo(20);
    }

    [Test]
    [Arguments(0, 20, 0)]
    [Arguments(1, 20, 5)]
    [Arguments(5, 20, 25)]
    [Arguments(10, 20, 50)]
    [Arguments(30, 20, 100)]
    public async Task CalcTimeProgress_reports_progress_through_a_segment(
        int currentSeconds, int durationSeconds, int expected)
    {
        var progress = SimulationPlan.CalcTimeProgress(
            TimeSpan.FromSeconds(currentSeconds), TimeSpan.FromSeconds(durationSeconds));

        await Assert.That(progress).IsEqualTo(expected);
    }

    [Test]
    public async Task A_negative_copies_count_is_rejected()
    {
        var plan = SimulationPlan.Create([Simulation.KeepConstant(-1, Time.Seconds(10))]);

        await Assert.That(plan.IsError).IsTrue();
        await Assert.That(plan.Error).IsTypeOf<LoadSimulationError.CopiesCountIsNegative>();
    }

    [Test]
    public async Task A_negative_rate_is_rejected()
    {
        var plan = SimulationPlan.Create([Simulation.Inject(-1, Time.Seconds(1), Time.Seconds(10))]);

        await Assert.That(plan.IsError).IsTrue();
        await Assert.That(plan.Error).IsTypeOf<LoadSimulationError.RateIsNegative>();
    }

    [Test]
    public async Task A_duration_below_the_minimum_is_rejected()
    {
        var plan = SimulationPlan.Create([Simulation.KeepConstant(1, TimeSpan.FromMilliseconds(10))]);

        await Assert.That(plan.IsError).IsTrue();
        await Assert.That(plan.Error).IsTypeOf<LoadSimulationError.DurationIsSmallerThanMin>();
    }

    [Test]
    public async Task An_interval_longer_than_the_simulation_is_rejected()
    {
        var plan = SimulationPlan.Create([Simulation.Inject(1, Time.Seconds(20), Time.Seconds(10))]);

        await Assert.That(plan.IsError).IsTrue();
        await Assert.That(plan.Error).IsTypeOf<LoadSimulationError.IntervalIsBiggerThanDuration>();
    }
}

/// <summary>
/// The fork point expressed load simulations as an F# discriminated union, so the compiler
/// proved that every switch over them handled every case. The C# closed hierarchy cannot
/// prove that, so these tests do: every case, through every function that switches on one.
/// A new simulation case fails here until it is handled everywhere.
/// </summary>
public class LoadSimulationExhaustivenessTests
{
    public static IEnumerable<Func<LoadSimulation>> AllCases() =>
    [
        () => Simulation.RampingConstant(1, Time.Seconds(10)),
        () => Simulation.KeepConstant(1, Time.Seconds(10)),
        () => Simulation.RampingInject(1, Time.Seconds(1), Time.Seconds(10)),
        () => Simulation.Inject(1, Time.Seconds(1), Time.Seconds(10)),
        () => Simulation.InjectRandom(1, 2, Time.Seconds(1), Time.Seconds(10)),
        () => Simulation.Pause(Time.Seconds(10))
    ];

    [Test]
    public async Task Every_case_the_hierarchy_declares_is_covered_by_these_tests()
    {
        var declaredCases = typeof(LoadSimulation)
            .GetNestedTypes()
            .Count(t => t.IsSubclassOf(typeof(LoadSimulation)));

        await Assert.That(AllCases().Count()).IsEqualTo(declaredCases);
    }

    [Test]
    [MethodDataSource(nameof(AllCases))]
    public async Task Every_case_has_a_duration(LoadSimulation simulation) =>
        await Assert.That(simulation.Duration).IsEqualTo(Time.Seconds(10));

    [Test]
    [MethodDataSource(nameof(AllCases))]
    public async Task Every_case_has_a_name(LoadSimulation simulation) =>
        await Assert.That(SimulationPlan.GetSimulationName(simulation)).IsNotEmpty();

    [Test]
    [MethodDataSource(nameof(AllCases))]
    public async Task Every_case_has_a_scheduling_interval(LoadSimulation simulation) =>
        await Assert.That(SimulationPlan.GetSimulationInterval(simulation) > TimeSpan.Zero).IsTrue();

    [Test]
    [MethodDataSource(nameof(AllCases))]
    public async Task Every_case_can_be_validated_and_planned(LoadSimulation simulation)
    {
        var plan = SimulationPlan.Create([simulation]);

        await Assert.That(plan.IsOk).IsTrue();
        await Assert.That(plan.Value.Count).IsEqualTo(1);
    }

    [Test]
    [MethodDataSource(nameof(AllCases))]
    public async Task Every_case_produces_simulation_stats(LoadSimulation simulation)
    {
        var stats = SimulationPlan.CreateSimulationStats(simulation, constantActorCount: 5, oneTimeActorCount: 7);

        await Assert.That(stats.SimulationName).IsEqualTo(SimulationPlan.GetSimulationName(simulation));
        await Assert.That(stats.Value >= 0).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(AllCases))]
    public async Task Every_case_can_be_scheduled(LoadSimulation simulation)
    {
        var plan = SimulationPlan.Create([simulation]).Value;

        var (command, count) = Internal.Domain.Scheduler.ScenarioScheduler.Schedule(
            (min, _) => min, plan[0], timeProgress: 50, currentConstActorCount: 1);

        await Assert.That(Enum.IsDefined(command)).IsTrue();
        await Assert.That(count >= 0).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(AllCases))]
    public async Task Every_case_can_be_rendered_in_a_report(LoadSimulation simulation)
    {
        var line = Internal.Services.Reports.ReportHelper.PrintLoadSimulation(x => x?.ToString() ?? "", simulation);

        await Assert.That(line).Contains(SimulationPlan.GetSimulationName(simulation));
    }
}
