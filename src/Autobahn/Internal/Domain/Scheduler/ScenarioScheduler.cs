using System.Diagnostics;
using Autobahn.Internal.Domain.Concurrency;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Stats;

namespace Autobahn.Internal.Domain.Scheduler;

/// <summary>What a scenario's load plan asks for at one point in time.</summary>
internal enum SchedulerCommand
{
    DoNothing,
    AddConstantActors,
    RemoveConstantActors,
    InjectOneTimeActors
}

/// <summary>How a scenario's run ended, and what it cost.</summary>
internal readonly record struct ScenarioShutdownResult(TimeSpan ExecutedDuration, int AbandonedIterations);

/// <summary>
/// Walks one scenario's load plan. On every simulation interval it works out how much load
/// should be live right now and delegates to the closed-model or open-model scheduler.
/// </summary>
internal sealed class ScenarioScheduler : IDisposable
{
    private readonly ScenarioContextArgs _scnCtx;
    private readonly ScenarioStatsActor _statsActor;
    private readonly CancellationToken _scnCancelToken;
    private readonly Stopwatch _scnTimer = new();
    private readonly ConstantActorScheduler _constantScheduler;
    private readonly OneTimeActorScheduler _oneTimeScheduler;

    private RuntimeScenario _scenario;
    private ITimer? _warmUpTimer;
    private SimulationPlanItem _currentSimulation;
    private long _pauseDurationTicks;
    private long _pauseSinceLastIntervalTicks;
    private int _abandonedIterations;
    private volatile bool _isWorking;

    public ScenarioScheduler(ScenarioContextArgs scnCtx)
    {
        _scnCtx = scnCtx;
        _statsActor = scnCtx.ScenarioStatsActor;
        _scnCancelToken = scnCtx.ScenarioCancellationToken.Token;
        _scenario = scnCtx.Scenario;
        _currentSimulation = _scenario.LoadSimulations[0];

        _constantScheduler = new ConstantActorScheduler(scnCtx);
        _oneTimeScheduler = new OneTimeActorScheduler(scnCtx);
    }

    public bool Working => _isWorking;
    public RuntimeScenario Scenario => _scenario;
    public IReadOnlyDictionary<TimeSpan, ScenarioStats> AllRealtimeStats => _statsActor.AllRealtimeStats;
    public ScenarioStats ConsoleScenarioStats => _statsActor.ConsoleScenarioStats;

    /// <summary>How many iterations were still running when the scenario gave up waiting.</summary>
    public int AbandonedIterations => Volatile.Read(ref _abandonedIterations);

    public Task Start(CancellationToken testHostCancelToken)
    {
        if (_scnCtx.ScenarioOperation == ScenarioOperation.WarmUp && _scenario.WarmUpDuration is { } warmUp)
            _warmUpTimer = _scnCtx.Time.CreateTimer(_ => _ = StopAsync(), null, warmUp, Timeout.InfiniteTimeSpan);

        return RunPlan(testHostCancelToken);
    }

    /// <summary>
    /// Ends the scenario: cancel, stop both actor schedulers, and wait a bounded time for the
    /// iterations already in flight to finish so they still count.
    /// </summary>
    /// <remarks>
    /// The fork point stopped synchronously and then polled once a second for up to a minute
    /// with no way to say what happened. This waits for the scenario's own completion timeout
    /// and reports how many iterations it gave up on, because an iteration abandoned mid-flight
    /// is a hole in the numbers that the operator should be told about rather than left to
    /// infer from a count that does not add up.
    /// </remarks>
    public async Task<ScenarioShutdownResult> StopAsync()
    {
        _scnCtx.ScenarioCancellationToken.Cancel();
        Stop();

        var abandoned = await WaitOnWorkingActors(_scenario.CompletionTimeout).ConfigureAwait(false);
        Volatile.Write(ref _abandonedIterations, abandoned);

        _constantScheduler.Dispose();
        _oneTimeScheduler.Dispose();

        return new ScenarioShutdownResult(_scenario.GetExecutedDuration(), abandoned);
    }

    public Task<ScenarioStats> BuildRealtimeStats(TimeSpan duration)
    {
        // Whatever of this interval was spent paused is not time the scenario had to work in,
        // so it comes off the window RPS is computed over.
        var pause = new TimeSpan(Interlocked.Exchange(ref _pauseSinceLastIntervalTicks, 0));
        return _statsActor.BuildReportingStats(GetCurrentSimulationStats(), duration, pause);
    }

    public Task<ScenarioStats> GetFinalStats() =>
        _statsActor.GetFinalStats(
            GetCurrentSimulationStats(),
            _scenario.GetExecutedDuration(),
            new TimeSpan(Interlocked.Read(ref _pauseDurationTicks)));

    public void Dispose()
    {
        _scnCtx.ScenarioCancellationToken.Cancel();
        Stop();
        _warmUpTimer?.Dispose();
        _constantScheduler.Dispose();
        _oneTimeScheduler.Dispose();
        _scnCtx.ScenarioCancellationToken.Dispose();
    }

    private LoadSimulationStats GetCurrentSimulationStats() =>
        SimulationPlan.CreateSimulationStats(
            _currentSimulation.Value,
            _constantScheduler.ScheduledActorCount,
            _oneTimeScheduler.ScheduledActorCount,
            WorkingActorCount());

    /// <summary>
    /// How many copies are mid-iteration right now, across both actor pools.
    /// </summary>
    /// <remarks>
    /// Counted rather than tracked: an actor's <c>Working</c> flag is already the truth, and a
    /// counter incremented and decremented on the hot path would cost every iteration
    /// something to answer a question asked once per reporting interval.
    /// </remarks>
    private int WorkingActorCount() =>
        ScenarioActorPool.GetWorkingActors(_constantScheduler.AvailableActors).Count()
        + ScenarioActorPool.GetWorkingActors(_oneTimeScheduler.AvailableActors).Count();

    private void Stop()
    {
        if (!_isWorking) return;

        _isWorking = false;

        // A scenario that reached the end of its plan executed the whole plan; one that was
        // cancelled executed only as far as its own clock got. A plan with a counted segment
        // has no planned length to fall back on, so its clock is the only answer either way.
        if (!_scnCtx.ScenarioCancellationToken.IsCancellationRequested && !_scenario.HasCountedSimulations)
        {
            _scnCtx.ScenarioCancellationToken.Cancel();
            _scenario = ScenarioFactory.SetExecutedDuration(_scenario, _scenario.PlanedDuration);
        }
        else
        {
            _scnCtx.ScenarioCancellationToken.Cancel();
            _scenario = ScenarioFactory.SetExecutedDuration(_scenario, _scnTimer.Elapsed);
        }

        _constantScheduler.AskToStop();
        _oneTimeScheduler.AskToStop();
        _scnTimer.Stop();
        _warmUpTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private async Task RunPlan(CancellationToken testHostCancelToken)
    {
        _isWorking = true;
        _scnTimer.Start();

        foreach (var simulation in _scenario.LoadSimulations)
        {
            _currentSimulation = simulation;

            var budget = simulation.Iterations is { } iterations ? new IterationBudget(iterations) : null;
            _scnCtx.IterationBudget = budget;

            await RunSegment(simulation, budget, testHostCancelToken).ConfigureAwait(false);

            _scnCtx.IterationBudget = null;

            if (!_isWorking || _scnCancelToken.IsCancellationRequested || testHostCancelToken.IsCancellationRequested)
                break;
        }

        Stop();
    }

    private async Task RunSegment(
        SimulationPlanItem simulation, IterationBudget? budget, CancellationToken testHostCancelToken)
    {
        var currentTime = TimeSpan.Zero;
        var isPause = simulation.Value is LoadSimulation.Pause;

        // Switching from a closed-model simulation to an open-model one leaves the
        // long-lived actors of the previous segment running; stop them first.
        var (cleanCommand, cleanCount) =
            ScheduleCleanPrevSimulation(simulation, _constantScheduler.ScheduledActorCount);

        if (cleanCommand == SchedulerCommand.RemoveConstantActors)
            _constantScheduler.RemoveActors(cleanCount);

        var simulationInterval = SimulationPlan.GetSimulationInterval(simulation.Value);

        // Copies are normally spread across the interval so they do not all fire at once. A
        // counted segment is the exception: its work is a fixed number of iterations rather
        // than a duration, so a copy that waits out the jitter can find the budget already
        // handed out and never run at all.
        var startJitter = simulation.IsCounted ? TimeSpan.Zero : simulationInterval;
        var intervalDrift = TimeSpan.Zero;

        while (_isWorking
               && !SegmentIsFinished(simulation, budget, currentTime)
               && !_scnCancelToken.IsCancellationRequested
               && !testHostCancelToken.IsCancellationRequested)
        {
            if (_statsActor.ScenarioFailCount >= _scenario.MaxFailCount)
            {
                Stop();
                _scnCtx.ExecStopCommand(new StopCommand.StopTest(
                    $"Stopping test because of too many fails. Scenario '{_scenario.ScenarioName}' "
                    + $"contains '{_statsActor.ScenarioFailCount}' fails."));
            }

            var startInterval = _scnTimer.Elapsed;
            var timeProgress = SimulationPlan.CalcTimeProgress(currentTime, simulation.Duration);

            var (command, copiesCount) = Schedule(
                GetRandomValue, simulation, timeProgress,
                _constantScheduler.ScheduledActorCount, budget?.RemainingToClaim);

            switch (command)
            {
                case SchedulerCommand.AddConstantActors:
                    _constantScheduler.AddActors(copiesCount, startJitter);
                    break;

                case SchedulerCommand.RemoveConstantActors:
                    _constantScheduler.RemoveActors(copiesCount);
                    break;

                case SchedulerCommand.InjectOneTimeActors:
                    if (copiesCount > 0) _oneTimeScheduler.InjectActors(copiesCount, startJitter);
                    break;

                case SchedulerCommand.DoNothing:
                    break;
            }

            try
            {
                // Scheduling took time; shorten the wait by the drift so the plan keeps its shape.
                var interval = simulationInterval - intervalDrift;

                await Task.Delay(
                        interval > TimeSpan.Zero ? interval : simulationInterval,
                        _scnCtx.Time,
                        _scnCancelToken)
                    .ConfigureAwait(false);

                intervalDrift = CalcTimeDrift(startInterval, _scnTimer.Elapsed, simulationInterval);

                currentTime += simulationInterval;
                _scnCtx.CurrentTimeBucket += simulationInterval;

                // Paused time is accumulated as it actually elapses, so a run stopped halfway
                // through a pause does not have the whole pause deducted from its throughput.
                if (isPause)
                {
                    Interlocked.Add(ref _pauseDurationTicks, simulationInterval.Ticks);
                    Interlocked.Add(ref _pauseSinceLastIntervalTicks, simulationInterval.Ticks);
                }
            }
            catch (OperationCanceledException)
            {
                // The scenario was stopped mid-interval; the loop condition ends the plan.
            }
        }
    }

    /// <summary>
    /// A timed segment ends when its duration is up. A counted one ends when every iteration
    /// has finished - or when none can finish, because they have all been handed out and no
    /// actor is still working on one.
    /// </summary>
    private bool SegmentIsFinished(SimulationPlanItem simulation, IterationBudget? budget, TimeSpan currentTime)
    {
        if (budget is null) return currentTime >= simulation.Duration;
        if (budget.IsFinished) return true;

        return budget.FullyClaimed && GetWorkingActorCount() == 0;
    }

    private static int GetRandomValue(int minRate, int maxRate) => Random.Shared.Next(minRate, maxRate);

    private int GetWorkingActorCount() =>
        ScenarioActorPool.GetWorkingActors(_constantScheduler.AvailableActors.Concat(_oneTimeScheduler.AvailableActors))
            .Count();

    /// <summary>
    /// Waits for the iterations already running to finish, and returns how many were still
    /// going when the wait ran out.
    /// </summary>
    private async Task<int> WaitOnWorkingActors(TimeSpan timeout)
    {
        // The session clock rather than a Stopwatch: this waits rather than measures, and a
        // test driving a run on a fake clock has to be able to move the deadline too.
        var startedAt = _scnCtx.Time.GetTimestamp();

        while (_scnCtx.Time.GetElapsedTime(startedAt) < timeout)
        {
            var working = GetWorkingActorCount();
            if (working == 0) return 0;

            await Task.Delay(Constants.ShutdownPollInterval, _scnCtx.Time).ConfigureAwait(false);
        }

        return GetWorkingActorCount();
    }

    /// <summary>How much longer the last interval took than it was supposed to.</summary>
    internal static TimeSpan CalcTimeDrift(TimeSpan startInterval, TimeSpan endInterval, TimeSpan simulationInterval)
    {
        var realDuration = endInterval - startInterval;
        return realDuration > simulationInterval ? realDuration - simulationInterval : TimeSpan.Zero;
    }

    /// <summary>Interpolates a ramp between the previous segment's level and this one's target.</summary>
    internal static int CalcScheduleByTime(int copiesCount, int prevSegmentCopiesCount, int timeSegmentProgress)
    {
        var value = copiesCount - prevSegmentCopiesCount;
        var result = value / 100.0 * timeSegmentProgress + prevSegmentCopiesCount;
        return (int)Math.Round(result, 0, MidpointRounding.AwayFromZero);
    }

    internal static (SchedulerCommand Command, int Count) Schedule(
        Func<int, int, int> getRandomValue,
        SimulationPlanItem simulation,
        int timeProgress,
        int currentConstActorCount,
        int? remainingToClaim = null)
    {
        switch (simulation.Value)
        {
            case LoadSimulation.RampingConstant x:
            {
                var scheduled = CalcScheduleByTime(x.Copies, simulation.PrevActorCount, timeProgress);
                return MoveConstantTo(scheduled, currentConstActorCount);
            }

            case LoadSimulation.KeepConstant x:
                return MoveConstantTo(x.Copies, currentConstActorCount);

            case LoadSimulation.IterationsForConstant x:
            {
                // Once every iteration has been handed out there is nothing left for a new
                // copy to do, so the pool winds down instead of being topped back up.
                var target = remainingToClaim is 0 ? 0 : Math.Min(x.Copies, remainingToClaim ?? x.Copies);
                return MoveConstantTo(target, currentConstActorCount);
            }

            case LoadSimulation.RampingInject x:
            {
                var scheduled = CalcScheduleByTime(x.Rate, simulation.PrevActorCount, timeProgress);
                return (SchedulerCommand.InjectOneTimeActors, Math.Abs(scheduled));
            }

            case LoadSimulation.Inject x:
                return (SchedulerCommand.InjectOneTimeActors, x.Rate);

            case LoadSimulation.InjectRandom x:
                return (SchedulerCommand.InjectOneTimeActors, getRandomValue(x.MinRate, x.MaxRate));

            case LoadSimulation.IterationsForInject x:
                return (SchedulerCommand.InjectOneTimeActors, Math.Min(x.Rate, remainingToClaim ?? x.Rate));

            case LoadSimulation.Pause:
                return currentConstActorCount > 0
                    ? (SchedulerCommand.RemoveConstantActors, currentConstActorCount)
                    : (SchedulerCommand.DoNothing, 0);

            default:
                throw new NotSupportedException($"Unknown load simulation: {simulation.Value.GetType().Name}");
        }
    }

    private static (SchedulerCommand Command, int Count) MoveConstantTo(int target, int current)
    {
        if (target > current) return (SchedulerCommand.AddConstantActors, target - current);
        if (target < current) return (SchedulerCommand.RemoveConstantActors, current - target);
        return (SchedulerCommand.DoNothing, 0);
    }

    /// <summary>Stops the closed-model actors left over when the plan switches to an open model.</summary>
    internal static (SchedulerCommand Command, int Count) ScheduleCleanPrevSimulation(
        SimulationPlanItem simulation, int currentConstActorCount)
    {
        if (currentConstActorCount <= 0) return (SchedulerCommand.DoNothing, 0);

        return simulation.Value switch
        {
            LoadSimulation.RampingConstant => (SchedulerCommand.DoNothing, 0),
            LoadSimulation.KeepConstant => (SchedulerCommand.DoNothing, 0),
            LoadSimulation.IterationsForConstant => (SchedulerCommand.DoNothing, 0),
            _ => (SchedulerCommand.RemoveConstantActors, currentConstActorCount)
        };
    }
}
