using BenchmarkDotNet.Attributes;
using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Scheduler;

namespace Autobahn.Benchmarks;

/// <summary>
/// The scheduling decision, taken once per simulation interval per scenario. Cheap by
/// construction; benchmarked so it stays that way, and so the switch over the simulation
/// hierarchy does not quietly start allocating.
/// </summary>
[MemoryDiagnoser]
public class SchedulerBenchmarks
{
    private SimulationPlanItem _rampingConstant = null!;
    private SimulationPlanItem _keepConstant = null!;
    private SimulationPlanItem _rampingInject = null!;
    private SimulationPlanItem _inject = null!;

    private static SimulationPlanItem Item(LoadSimulation value) =>
        SimulationPlan.Create("bench", [value]).Value[0];

    [GlobalSetup]
    public void Setup()
    {
        _rampingConstant = Item(Simulation.RampingConstant(500, TimeSpan.FromMinutes(1)));
        _keepConstant = Item(Simulation.KeepConstant(500, TimeSpan.FromMinutes(1)));
        _rampingInject = Item(Simulation.RampingInject(500, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)));
        _inject = Item(Simulation.Inject(500, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)));
    }

    private static int Random(int min, int max) => min;

    [Benchmark] public int ScheduleRampingConstant() => ScenarioScheduler.Schedule(Random, _rampingConstant, 50, 100).Count;
    [Benchmark] public int ScheduleKeepConstant() => ScenarioScheduler.Schedule(Random, _keepConstant, 50, 100).Count;
    [Benchmark] public int ScheduleRampingInject() => ScenarioScheduler.Schedule(Random, _rampingInject, 50, 100).Count;
    [Benchmark] public int ScheduleInject() => ScenarioScheduler.Schedule(Random, _inject, 50, 100).Count;

    /// <summary>Returns the segment count rather than the plan: the plan type is internal.</summary>
    [Benchmark]
    public int BuildLoadPlan() =>
        SimulationPlan.Create("bench",
        [
            Simulation.RampingConstant(50, TimeSpan.FromSeconds(30)),
            Simulation.KeepConstant(50, TimeSpan.FromMinutes(5)),
            Simulation.RampingInject(100, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)),
            Simulation.Inject(100, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5)),
            Simulation.Pause(TimeSpan.FromSeconds(10))
        ]).Value.Count;
}
