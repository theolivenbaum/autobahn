using System.Runtime.CompilerServices;
using Autobahn.Stats;

namespace Autobahn.Internal.Domain;

/// <summary>
/// Validates a scenario's load simulations and expands them onto a timeline.
/// </summary>
internal static class SimulationPlan
{
    /// <summary>Validates one simulation's numbers and durations.</summary>
    private static Result<LoadSimulation> Validate(LoadSimulation simulation)
    {
        Result<LoadSimulation>? CheckCopies(int copies) =>
            copies < 0 ? Result<LoadSimulation>.Fail(new LoadSimulationError.CopiesCountIsNegative(simulation)) : null;

        Result<LoadSimulation>? CheckRate(int rate) =>
            rate < 0 ? Result<LoadSimulation>.Fail(new LoadSimulationError.RateIsNegative(simulation)) : null;

        Result<LoadSimulation>? CheckInterval(TimeSpan interval, TimeSpan duration) =>
            interval > duration ? Result<LoadSimulation>.Fail(new LoadSimulationError.IntervalIsBiggerThanDuration(simulation)) : null;

        Result<LoadSimulation>? CheckDuration(TimeSpan duration) =>
            duration < Constants.MinSimulationDuration ? Result<LoadSimulation>.Fail(new LoadSimulationError.DurationIsSmallerThanMin(simulation)) : null;

        var failure = simulation switch
        {
            LoadSimulation.RampingConstant x => CheckCopies(x.Copies) ?? CheckDuration(x.During),
            LoadSimulation.KeepConstant x    => CheckCopies(x.Copies) ?? CheckDuration(x.During),

            LoadSimulation.RampingInject x => CheckRate(x.Rate) ?? CheckInterval(x.Interval, x.During) ?? CheckDuration(x.During),
            LoadSimulation.Inject x        => CheckRate(x.Rate) ?? CheckInterval(x.Interval, x.During) ?? CheckDuration(x.During),

            LoadSimulation.InjectRandom x =>
                CheckRate(x.MinRate) ?? CheckRate(x.MaxRate) ?? CheckInterval(x.Interval, x.During) ?? CheckDuration(x.During),

            LoadSimulation.Pause x => CheckDuration(x.During),

            _ => throw new NotSupportedException($"Unknown load simulation: {simulation.GetType().Name}")
        };

        return failure ?? Result<LoadSimulation>.Ok(simulation);
    }

    /// <summary>Validates a whole list, reporting the first problem found.</summary>
    internal static Result<List<LoadSimulation>> ValidateAll(IReadOnlyList<LoadSimulation> simulations) =>
        Result.Sequence(simulations.Select(Validate));

    private static SimulationPlanItem CreateItem(TimeSpan startTime, int prevCopiesCount, LoadSimulation simulation)
    {
        var duration = simulation.Duration;

        // A random-injection segment ramps from nothing rather than from whatever the previous
        // segment left running, so its PrevActorCount is always zero.
        var prevActorCount = simulation is LoadSimulation.InjectRandom ? 0 : prevCopiesCount;

        return new SimulationPlanItem
        {
            Value = simulation,
            StartTime = startTime,
            EndTime = startTime + duration,
            Duration = duration,
            PrevActorCount = prevActorCount
        };
    }

    /// <summary>The actor count a segment leaves behind for the next one to ramp from.</summary>
    private static int GetPrevCopiesCount(LoadSimulation simulation) => simulation switch
    {
        LoadSimulation.RampingConstant x => x.Copies,
        LoadSimulation.KeepConstant x    => x.Copies,
        LoadSimulation.RampingInject x   => x.Rate,
        LoadSimulation.Inject x          => x.Rate,
        LoadSimulation.InjectRandom x    => x.MaxRate,
        LoadSimulation.Pause             => 0,
        _ => throw new NotSupportedException($"Unknown load simulation: {simulation.GetType().Name}")
    };

    /// <summary>
    /// Validates the simulations and lays them out end to end on the scenario's timeline.
    /// </summary>
    /// <remarks>
    /// An empty list produces an empty plan rather than an error, which is what the fork point
    /// did: it built an <c>EmptySimulationsList</c> error but discarded it. The error is kept
    /// here because TODO.md section 3 ("Load-plan validation") is where that gets fixed properly,
    /// together with the other plan-level checks - changing it as a side effect of the port would
    /// hide a behaviour change inside a translation.
    /// </remarks>
    public static Result<List<SimulationPlanItem>> Create(IReadOnlyList<LoadSimulation> simulations)
    {
        var validated = ValidateAll(simulations);
        if (validated.IsError) return Result<List<SimulationPlanItem>>.Fail(validated.Error);

        var plan = new List<SimulationPlanItem>(validated.Value.Count);

        // The plan starts from a zero-length placeholder, so the first real segment starts at zero
        // with no previous actors; the placeholder itself is never emitted.
        var previous = CreateItem(TimeSpan.Zero, 0, new LoadSimulation.KeepConstant(0, TimeSpan.Zero));

        foreach (var simulation in validated.Value)
        {
            var item = CreateItem(previous.EndTime, GetPrevCopiesCount(previous.Value), simulation);
            if (item.EndTime > TimeSpan.Zero) plan.Add(item);
            previous = item;
        }

        return Result<List<SimulationPlanItem>>.Ok(plan);
    }

    public static TimeSpan GetPlanedDuration(IEnumerable<SimulationPlanItem> plan) =>
        plan.Aggregate(TimeSpan.Zero, (total, item) => total + item.Duration);

    /// <summary>How often the scheduler re-evaluates this simulation.</summary>
    public static TimeSpan GetSimulationInterval(LoadSimulation simulation) => simulation switch
    {
        LoadSimulation.RampingInject x => x.Interval,
        LoadSimulation.Inject x        => x.Interval,
        LoadSimulation.InjectRandom x  => x.Interval,
        _ => Constants.OneSecond
    };

    public static string GetSimulationName(LoadSimulation simulation) => simulation switch
    {
        LoadSimulation.RampingConstant => "ramping_constant",
        LoadSimulation.KeepConstant    => "keep_constant",
        LoadSimulation.RampingInject   => "ramping_inject",
        LoadSimulation.Inject          => "inject",
        LoadSimulation.InjectRandom    => "inject_random",
        LoadSimulation.Pause           => "pause",
        _ => throw new NotSupportedException($"Unknown load simulation: {simulation.GetType().Name}")
    };

    /// <summary>How far through a segment the run is, as a percentage clamped to 100.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalcTimeProgress(TimeSpan currentTime, TimeSpan duration)
    {
        var ratio = currentTime.TotalMilliseconds / duration.TotalMilliseconds * 100.0;
        var progress = (int)Math.Round(ratio, 0, MidpointRounding.AwayFromZero);
        return progress > 100 ? 100 : progress;
    }

    /// <summary>The load level to report: constant actors for closed models, injected actors for open ones.</summary>
    public static LoadSimulationStats CreateSimulationStats(
        LoadSimulation simulation, int constantActorCount, int oneTimeActorCount)
    {
        var value = simulation switch
        {
            LoadSimulation.RampingConstant => constantActorCount,
            LoadSimulation.KeepConstant    => constantActorCount,
            LoadSimulation.RampingInject   => oneTimeActorCount,
            LoadSimulation.Inject          => oneTimeActorCount,
            LoadSimulation.InjectRandom    => oneTimeActorCount,
            LoadSimulation.Pause           => 0,
            _ => throw new NotSupportedException($"Unknown load simulation: {simulation.GetType().Name}")
        };

        return new LoadSimulationStats { SimulationName = GetSimulationName(simulation), Value = value };
    }
}
