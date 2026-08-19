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
    private Timer? _warmUpTimer;
    private SimulationPlanItem _currentSimulation;
    private TimeSpan _pauseDuration = TimeSpan.Zero;
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

    public Task Start(CancellationToken testHostCancelToken)
    {
        if (_scnCtx.ScenarioOperation == ScenarioOperation.WarmUp && _scenario.WarmUpDuration is { } warmUp)
            _warmUpTimer = new Timer(_ => Stop(), null, warmUp, Timeout.InfiniteTimeSpan);

        return RunPlan(testHostCancelToken);
    }

    /// <summary>Cancels the scenario and waits, with a bound, for its in-flight iterations.</summary>
    public Task StopAsync()
    {
        _scnCtx.ScenarioCancellationToken.Cancel();
        Stop();
        return WaitOnWorkingActors();
    }

    public Task<ScenarioStats> BuildRealtimeStats(TimeSpan duration) =>
        _statsActor.BuildReportingStats(GetCurrentSimulationStats(), duration);

    public Task<ScenarioStats> GetFinalStats() =>
        _statsActor.GetFinalStats(GetCurrentSimulationStats(), _scenario.GetExecutedDuration(), _pauseDuration);

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
            _oneTimeScheduler.ScheduledActorCount);

    private void Stop()
    {
        if (!_isWorking) return;

        _isWorking = false;

        // A scenario that reached the end of its plan executed the whole plan; one that was
        // cancelled executed only as far as its own clock got.
        if (!_scnCtx.ScenarioCancellationToken.IsCancellationRequested)
        {
            _scnCtx.ScenarioCancellationToken.Cancel();
            _scenario = ScenarioFactory.SetExecutedDuration(_scenario, _scenario.PlanedDuration);
        }
        else
        {
            _scenario = ScenarioFactory.SetExecutedDuration(_scenario, _scnTimer.Elapsed);
        }

        _constantScheduler.AskToStop();
        _oneTimeScheduler.AskToStop();
        _scnTimer.Stop();
        _warmUpTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private async Task RunPlan(CancellationToken testHostCancelToken)
    {
        _isWorking = true;
        _scnTimer.Start();

        foreach (var simulation in _scenario.LoadSimulations)
        {
            var currentTime = TimeSpan.Zero;
            _currentSimulation = simulation;

            // Switching from a closed-model simulation to an open-model one leaves the
            // long-lived actors of the previous segment running; stop them first.
            var (cleanCommand, cleanCount) =
                ScheduleCleanPrevSimulation(simulation, _constantScheduler.ScheduledActorCount);

            if (cleanCommand == SchedulerCommand.RemoveConstantActors)
                _constantScheduler.RemoveActors(cleanCount);

            var simulationInterval = SimulationPlan.GetSimulationInterval(simulation.Value);
            var intervalDrift = TimeSpan.Zero;

            while (_isWorking
                   && currentTime < simulation.Duration
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

                var (command, copiesCount) =
                    Schedule(GetRandomValue, simulation, timeProgress, _constantScheduler.ScheduledActorCount);

                switch (command)
                {
                    case SchedulerCommand.AddConstantActors:
                        _constantScheduler.AddActors(copiesCount, simulationInterval);
                        break;

                    case SchedulerCommand.RemoveConstantActors:
                        _constantScheduler.RemoveActors(copiesCount);
                        break;

                    case SchedulerCommand.InjectOneTimeActors:
                        _oneTimeScheduler.InjectActors(copiesCount, simulationInterval);
                        break;

                    case SchedulerCommand.DoNothing:
                        break;
                }

                try
                {
                    // Scheduling took time; shorten the wait by the drift so the plan keeps its shape.
                    var interval = simulationInterval - intervalDrift;

                    await Task.Delay(interval > TimeSpan.Zero ? interval : simulationInterval, _scnCancelToken)
                        .ConfigureAwait(false);

                    intervalDrift = CalcTimeDrift(startInterval, _scnTimer.Elapsed, simulationInterval);

                    currentTime += simulationInterval;
                    _scnCtx.CurrentTimeBucket += simulationInterval;
                }
                catch (OperationCanceledException)
                {
                    // The scenario was stopped mid-interval; the loop condition ends the plan.
                }
            }

            // Time spent paused is excluded from the window RPS is computed over.
            if (simulation.Value is LoadSimulation.Pause pause)
                _pauseDuration += pause.During;
        }

        Stop();
    }

    private static int GetRandomValue(int minRate, int maxRate) => Random.Shared.Next(minRate, maxRate);

    private int GetWorkingActorCount() =>
        ScenarioActorPool.GetWorkingActors(_constantScheduler.AvailableActors.Concat(_oneTimeScheduler.AvailableActors))
            .Count();

    private async Task WaitOnWorkingActors()
    {
        var counter = 0;

        while (counter < Constants.MaxWaitWorkingActorsSec)
        {
            if (GetWorkingActorCount() > 0)
            {
                await Task.Delay(Constants.OneSecond).ConfigureAwait(false);
                counter++;
            }
            else
            {
                counter = Constants.MaxWaitWorkingActorsSec;
            }
        }
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
        int currentConstActorCount)
    {
        switch (simulation.Value)
        {
            case LoadSimulation.RampingConstant x:
            {
                var scheduled = CalcScheduleByTime(x.Copies, simulation.PrevActorCount, timeProgress);
                var scheduleNow = scheduled - currentConstActorCount;

                if (scheduleNow > 0) return (SchedulerCommand.AddConstantActors, scheduleNow);
                if (scheduleNow < 0) return (SchedulerCommand.RemoveConstantActors, Math.Abs(scheduleNow));
                return (SchedulerCommand.DoNothing, 0);
            }

            case LoadSimulation.KeepConstant x:
                if (currentConstActorCount < x.Copies)
                    return (SchedulerCommand.AddConstantActors, x.Copies - currentConstActorCount);
                if (currentConstActorCount > x.Copies)
                    return (SchedulerCommand.RemoveConstantActors, currentConstActorCount - x.Copies);
                return (SchedulerCommand.DoNothing, 0);

            case LoadSimulation.RampingInject x:
            {
                var scheduled = CalcScheduleByTime(x.Rate, simulation.PrevActorCount, timeProgress);
                return (SchedulerCommand.InjectOneTimeActors, Math.Abs(scheduled));
            }

            case LoadSimulation.Inject x:
                return (SchedulerCommand.InjectOneTimeActors, x.Rate);

            case LoadSimulation.InjectRandom x:
                return (SchedulerCommand.InjectOneTimeActors, getRandomValue(x.MinRate, x.MaxRate));

            case LoadSimulation.Pause:
                return currentConstActorCount > 0
                    ? (SchedulerCommand.RemoveConstantActors, currentConstActorCount)
                    : (SchedulerCommand.DoNothing, 0);

            default:
                throw new NotSupportedException($"Unknown load simulation: {simulation.Value.GetType().Name}");
        }
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
            _ => (SchedulerCommand.RemoveConstantActors, currentConstActorCount)
        };
    }
}
