using System.Runtime.CompilerServices;
using Autobahn.Stats;

namespace Autobahn.Internal.Domain;

/// <summary>
/// Validates a scenario's load simulations and expands them onto a timeline.
/// </summary>
internal static class SimulationPlan
{
    /// <summary>Validates one simulation's numbers and durations.</summary>
    private static Result<LoadSimulation> Validate(string scenarioName, LoadSimulation simulation)
    {
        Result<LoadSimulation>? Fail(LoadSimulationError error) => Result<LoadSimulation>.Fail(error);

        Result<LoadSimulation>? CheckCopies(int copies) =>
            copies < 0 ? Fail(new LoadSimulationError.CopiesCountIsNegative(scenarioName, simulation)) : null;

        Result<LoadSimulation>? CheckRate(int rate) =>
            rate < 0 ? Fail(new LoadSimulationError.RateIsNegative(scenarioName, simulation)) : null;

        Result<LoadSimulation>? CheckInterval(TimeSpan interval, TimeSpan duration) =>
            interval > duration ? Fail(new LoadSimulationError.IntervalIsBiggerThanDuration(scenarioName, simulation)) : null;

        Result<LoadSimulation>? CheckIntervalIsPositive(TimeSpan interval) =>
            interval <= TimeSpan.Zero ? Fail(new LoadSimulationError.IntervalIsNotPositive(scenarioName, simulation)) : null;

        Result<LoadSimulation>? CheckDuration(TimeSpan duration) =>
            duration < Constants.MinSimulationDuration
                ? Fail(new LoadSimulationError.DurationIsSmallerThanMin(scenarioName, simulation))
                : null;

        Result<LoadSimulation>? CheckIterations(int iterations) =>
            iterations <= 0 ? Fail(new LoadSimulationError.IterationsCountIsNotPositive(scenarioName, simulation)) : null;

        var failure = simulation switch
        {
            LoadSimulation.RampingConstant x => CheckCopies(x.Copies) ?? CheckDuration(x.During),
            LoadSimulation.KeepConstant x => CheckCopies(x.Copies) ?? CheckDuration(x.During),

            LoadSimulation.RampingInject x =>
                CheckRate(x.Rate) ?? CheckIntervalIsPositive(x.Interval)
                ?? CheckInterval(x.Interval, x.During) ?? CheckDuration(x.During),

            LoadSimulation.Inject x =>
                CheckRate(x.Rate) ?? CheckIntervalIsPositive(x.Interval)
                ?? CheckInterval(x.Interval, x.During) ?? CheckDuration(x.During),

            LoadSimulation.InjectRandom x =>
                CheckRate(x.MinRate) ?? CheckRate(x.MaxRate)
                ?? (x.MinRate >= x.MaxRate
                    ? Fail(new LoadSimulationError.RandomRatesAreNotAscending(scenarioName, simulation))
                    : null)
                ?? CheckIntervalIsPositive(x.Interval)
                ?? CheckInterval(x.Interval, x.During) ?? CheckDuration(x.During),

            LoadSimulation.IterationsForConstant x => CheckCopies(x.Copies) ?? CheckIterations(x.Iterations),

            LoadSimulation.IterationsForInject x =>
                CheckRate(x.Rate) ?? CheckIntervalIsPositive(x.Interval) ?? CheckIterations(x.Iterations),

            LoadSimulation.Pause x => CheckDuration(x.During),

            _ => throw new NotSupportedException($"Unknown load simulation: {simulation.GetType().Name}")
        };

        return failure ?? Result<LoadSimulation>.Ok(simulation);
    }

    /// <summary>Validates a whole list, reporting the first problem found.</summary>
    internal static Result<List<LoadSimulation>> ValidateAll(
        string scenarioName, IReadOnlyList<LoadSimulation> simulations)
    {
        if (simulations.Count == 0)
            return Result<List<LoadSimulation>>.Fail(new LoadSimulationError.EmptySimulationsList(scenarioName));

        return Result.Sequence(simulations.Select(x => Validate(scenarioName, x)));
    }

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
        LoadSimulation.KeepConstant x => x.Copies,
        LoadSimulation.RampingInject x => x.Rate,
        LoadSimulation.Inject x => x.Rate,
        LoadSimulation.InjectRandom x => x.MaxRate,
        LoadSimulation.IterationsForConstant x => x.Copies,
        LoadSimulation.IterationsForInject x => x.Rate,
        LoadSimulation.Pause => 0,
        _ => throw new NotSupportedException($"Unknown load simulation: {simulation.GetType().Name}")
    };

    /// <summary>The most copies a segment ever has alive at once.</summary>
    internal static int GetMaxCopiesCount(LoadSimulation simulation) => GetPrevCopiesCount(simulation);

    /// <summary>
    /// Validates the simulations and lays them out end to end on the scenario's timeline.
    /// </summary>
    /// <remarks>
    /// Counted segments have no duration the plan can know, so they occupy a zero-length slot
    /// on the timeline and the following segment starts where they did. Elapsed time still
    /// advances while one runs; it is the <em>planned</em> timeline that cannot account for it.
    /// </remarks>
    public static Result<List<SimulationPlanItem>> Create(
        string scenarioName, IReadOnlyList<LoadSimulation> simulations)
    {
        var validated = ValidateAll(scenarioName, simulations);
        if (validated.IsError) return Result<List<SimulationPlanItem>>.Fail(validated.Error);

        var plan = new List<SimulationPlanItem>(validated.Value.Count);

        var startTime = TimeSpan.Zero;
        var prevCopiesCount = 0;

        foreach (var simulation in validated.Value)
        {
            var item = CreateItem(startTime, prevCopiesCount, simulation);

            plan.Add(item);

            startTime = item.EndTime;
            prevCopiesCount = GetPrevCopiesCount(simulation);
        }

        return Result<List<SimulationPlanItem>>.Ok(plan);
    }

    /// <summary>
    /// How long the plan says the scenario runs for. Counted segments contribute nothing,
    /// because how long they take is up to the target rather than the plan.
    /// </summary>
    public static TimeSpan GetPlanedDuration(IEnumerable<SimulationPlanItem> plan) =>
        plan.Aggregate(TimeSpan.Zero, (total, item) => total + item.Duration);

    /// <summary>The most copies this plan ever runs at once, across all of its segments.</summary>
    public static int GetMaxCopiesCount(IEnumerable<SimulationPlanItem> plan) =>
        plan.Select(x => GetMaxCopiesCount(x.Value)).DefaultIfEmpty(0).Max();

    /// <summary>How often the scheduler re-evaluates this simulation.</summary>
    public static TimeSpan GetSimulationInterval(LoadSimulation simulation) => simulation switch
    {
        LoadSimulation.RampingInject x => x.Interval,
        LoadSimulation.Inject x => x.Interval,
        LoadSimulation.InjectRandom x => x.Interval,
        LoadSimulation.IterationsForInject x => x.Interval,
        _ => Constants.OneSecond
    };

    public static string GetSimulationName(LoadSimulation simulation) => simulation switch
    {
        LoadSimulation.RampingConstant => "ramping_constant",
        LoadSimulation.KeepConstant => "keep_constant",
        LoadSimulation.RampingInject => "ramping_inject",
        LoadSimulation.Inject => "inject",
        LoadSimulation.InjectRandom => "inject_random",
        LoadSimulation.IterationsForConstant => "iterations_for_constant",
        LoadSimulation.IterationsForInject => "iterations_for_inject",
        LoadSimulation.Pause => "pause",
        _ => throw new NotSupportedException($"Unknown load simulation: {simulation.GetType().Name}")
    };

    /// <summary>How far through a segment the run is, as a percentage clamped to 100.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CalcTimeProgress(TimeSpan currentTime, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return 100;

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
            LoadSimulation.KeepConstant => constantActorCount,
            LoadSimulation.IterationsForConstant => constantActorCount,
            LoadSimulation.RampingInject => oneTimeActorCount,
            LoadSimulation.Inject => oneTimeActorCount,
            LoadSimulation.InjectRandom => oneTimeActorCount,
            LoadSimulation.IterationsForInject => oneTimeActorCount,
            LoadSimulation.Pause => 0,
            _ => throw new NotSupportedException($"Unknown load simulation: {simulation.GetType().Name}")
        };

        return new LoadSimulationStats { SimulationName = GetSimulationName(simulation), Value = value };
    }

    /// <summary>
    /// Rescales a plan so the scenario contributes its declared share of the combined load.
    /// </summary>
    /// <remarks>
    /// Applied to the segments rather than to the running actor count, so a ramp interpolates
    /// between two already-scaled numbers and stays correct all the way up. A share that
    /// rounds a segment down to nothing still runs one copy: a scenario that was asked for is
    /// a scenario that should appear in the results.
    /// </remarks>
    public static IReadOnlyList<LoadSimulation> ApplyWeight(
        IReadOnlyList<LoadSimulation> simulations, int weight, int totalWeight)
    {
        if (totalWeight <= 0 || weight == totalWeight) return simulations;

        return simulations.Select(Scale).ToArray();

        int ScaleValue(int value) =>
            value <= 0 ? value : Math.Max(1, (int)Math.Round((double)value * weight / totalWeight, MidpointRounding.AwayFromZero));

        LoadSimulation Scale(LoadSimulation simulation) => simulation switch
        {
            LoadSimulation.RampingConstant x => x with { Copies = ScaleValue(x.Copies) },
            LoadSimulation.KeepConstant x => x with { Copies = ScaleValue(x.Copies) },
            LoadSimulation.RampingInject x => x with { Rate = ScaleValue(x.Rate) },
            LoadSimulation.Inject x => x with { Rate = ScaleValue(x.Rate) },

            LoadSimulation.InjectRandom x => x with
            {
                MinRate = ScaleValue(x.MinRate),
                MaxRate = Math.Max(ScaleValue(x.MinRate) + 1, ScaleValue(x.MaxRate))
            },

            // The iteration count is what the author asked to run, not a rate to divide up;
            // only the concurrency it runs at is a share of the combined load.
            LoadSimulation.IterationsForConstant x => x with { Copies = ScaleValue(x.Copies) },
            LoadSimulation.IterationsForInject x => x with { Rate = ScaleValue(x.Rate) },

            LoadSimulation.Pause => simulation,
            _ => throw new NotSupportedException($"Unknown load simulation: {simulation.GetType().Name}")
        };
    }
}
