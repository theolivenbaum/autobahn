using System.Collections.Concurrent;
using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Scheduler;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Internal.Domain.Thresholds;
using Autobahn.Internal.Infra;
using Autobahn.Stats;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Autobahn.Internal.Services.TestHost;

/// <summary>Produces interval statistics on a timer, and the session result at the end.</summary>
internal interface IReportingManager : IDisposable
{
    Task Start();
    Task Stop();
    Task<SessionResult> GetSessionResult(HostInfo hostInfo);
}

/// <summary>What a scheduler-closing tick produced, before it reaches the timeline.</summary>
internal readonly record struct IntervalSnapshot(
    TimeSpan Duration, IReadOnlyList<ScenarioStats> ScenarioStats, IReadOnlyList<MetricStats> Metrics);

/// <summary>
/// Ticks at the reporting interval and asks each scheduler to close its interval, which is
/// what feeds the live console table and the timeline behind the final report.
/// </summary>
internal sealed class ReportingManager : IReportingManager
{
    private readonly IGlobalDependency _dep;
    private readonly IReadOnlyList<ScenarioScheduler> _schedulers;
    private readonly SessionArgs _sessionArgs;
    private readonly TimeSpan _reportingInterval;
    private readonly TimeSpan _timerMaxDuration;
    private readonly ITimer _buildRealtimeStatsTimer;

    // What the metrics did in each closed interval, keyed the same way the scheduler stats
    // are so the two line up in the timeline.
    private readonly ConcurrentDictionary<TimeSpan, MetricStats[]> _intervalMetrics = new();

    // And where each threshold stood when that interval was checked. A rule that passed, failed
    // for a minute and recovered is a different run from one that failed at the end, and the
    // timeline is the only place that difference survives.
    private readonly ConcurrentDictionary<TimeSpan, ThresholdResult[]> _intervalThresholds = new();

    private TimeSpan _curDuration = TimeSpan.Zero;

    /// <summary>
    /// Called when a threshold has failed often enough in a row to end the run. Set by the
    /// test host, which is the only thing that knows how to stop one.
    /// </summary>
    public Action<string>? OnThresholdAbort { get; set; }

    public ThresholdChecker Thresholds { get; }

    public ReportingManager(IGlobalDependency dep, IReadOnlyList<ScenarioScheduler> schedulers, SessionArgs sessionArgs)
    {
        _dep = dep;
        Thresholds = new ThresholdChecker(
            sessionArgs.Thresholds, schedulers.Select(x => x.Scenario.ScenarioName).ToArray());

        _schedulers = schedulers;
        _sessionArgs = sessionArgs;
        _reportingInterval = sessionArgs.ReportingInterval;
        // A plan with a counted segment has no length to stop the timer at, so it ticks until
        // the run itself ends.
        _timerMaxDuration = schedulers.Any(x => x.Scenario.HasCountedSimulations)
            ? TimeSpan.MaxValue
            : schedulers.Max(x => x.Scenario.PlanedDuration);

        // Created stopped: Start() is what puts it in step with the run.
        _buildRealtimeStatsTimer = dep.Time.CreateTimer(
            OnElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnElapsed(object? state)
    {
        var duration = _curDuration + _reportingInterval;

        if (duration > _timerMaxDuration)
        {
            StopTicking();
            return;
        }

        _curDuration = duration;

        var metrics = _dep.Metrics.CloseInterval();
        _intervalMetrics[duration] = metrics;

        // Fire and forget: the schedulers answer on their own actors, and a slow answer must
        // never hold up the next tick or the run itself. The thresholds are checked once the
        // interval's stats actually exist, which is why it happens in the continuation.
        _ = Task.WhenAll(_schedulers.Select(x => x.BuildRealtimeStats(duration)))
            .ContinueWith(
                task => OnIntervalClosed(duration, task, metrics),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private void OnIntervalClosed(TimeSpan duration, Task<ScenarioStats[]> statsTask, MetricStats[] metrics)
    {
        TestHostConsole.LiveStatusTable.PrintIntervalProgress(_dep, duration, statsTask.Result);
        CheckThresholds(duration, statsTask, metrics);

        // After the check, not before: the observer's copy of this interval should say where
        // the thresholds stood at the end of it, which is what was just worked out.
        var thresholds = Thresholds.IsEmpty ? [] : Thresholds.GetResults();
        _intervalThresholds[duration] = thresholds;

        NotifyObserver(duration, statsTask.Result, metrics, thresholds);
    }

    /// <summary>
    /// Hands the closed interval to whoever asked for it, without waiting.
    /// </summary>
    /// <remarks>
    /// Not awaited on purpose: an observer shipping the numbers over a network must not be
    /// able to hold up the next tick, let alone the run. A failure is logged and the run
    /// carries on - an export that broke is not a reason to lose the test.
    /// </remarks>
    private void NotifyObserver(
        TimeSpan duration, ScenarioStats[] scenarioStats, MetricStats[] metrics, ThresholdResult[] thresholds)
    {
        if (_sessionArgs.OnInterval is not { } observe) return;

        var record = new TimeLineHistoryRecord
        {
            ScenarioStats = scenarioStats,
            Metrics = metrics,
            Thresholds = thresholds,
            Duration = duration
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await observe(record).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _dep.Logger.ZLogError($"The interval observer failed: {ex}");
            }
        });
    }

    private void CheckThresholds(TimeSpan duration, Task<ScenarioStats[]> statsTask, MetricStats[] metrics)
    {
        if (Thresholds.IsEmpty) return;

        try
        {
            var result = Thresholds.Check(duration, statsTask.Result, metrics);
            if (!result.ShouldAbort) return;

            foreach (var reason in result.AbortReasons) _dep.LogError(reason);

            OnThresholdAbort?.Invoke(Constants.StopReasonThreshold);
        }
        catch (Exception ex)
        {
            // A broken rule must not take the run with it; the final check will report it.
            _dep.Logger.ZLogError($"Threshold check failed: {ex}");
        }
    }

    /// <summary>
    /// Starts ticking with the run, not a few seconds into it.
    /// </summary>
    /// <remarks>
    /// The fork point waited three seconds first, which put every data point three seconds out
    /// of step with its own label and made the first window cover eight seconds of traffic
    /// while claiming to cover five. Starting immediately means tick N happens N intervals in
    /// and is labelled N intervals, which is the only way two data points are comparable.
    /// </remarks>
    public Task Start()
    {
        _buildRealtimeStatsTimer.Change(_reportingInterval, _reportingInterval);
        return Task.CompletedTask;
    }

    private void StopTicking() =>
        _buildRealtimeStatsTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Stops ticking, then waits just long enough for measurements already in flight to reach
    /// the stats actor before the final statistics are built.
    /// </summary>
    /// <remarks>
    /// Stopping the timer first is what makes the last window honest: a tick fired after the
    /// run ended would emit a partial window labelled as a full one. Whatever traffic falls
    /// inside that partial window is still counted - it is in the global accumulator the final
    /// report reads, just not in the timeline as an interval it did not fill.
    /// </remarks>
    public async Task Stop()
    {
        StopTicking();
        await Task.Delay(Constants.ReportingManagerDrainDelay, _dep.Time).ConfigureAwait(false);
    }

    public async Task<SessionResult> GetSessionResult(HostInfo hostInfo)
    {
        var history = TimeLineHistory.Create(
            _schedulers.Select(x => x.AllRealtimeStats), _intervalMetrics, _intervalThresholds);
        var finalStats = await GetFinalStats(hostInfo).ConfigureAwait(false);
        var hints = GetHints(finalStats);

        return new SessionResult { FinalStats = finalStats, TimeLineHistory = history, Hints = hints };
    }

    private async Task<SessionStats> GetFinalStats(HostInfo hostInfo)
    {
        var scenarioStats = await Task.WhenAll(_schedulers.Select(x => x.GetFinalStats())).ConfigureAwait(false);
        var sessionStats = Statistics.CreateSessionStats(_sessionArgs.TestInfo, hostInfo, scenarioStats);
        var pluginStats = await WorkerPlugins.GetStats(_dep, sessionStats).ConfigureAwait(false);
        var metrics = _dep.Metrics.Global();

        // The last check is against the whole run, not the last interval: a rule about the
        // run's error rate is a claim about all of it, and a run shorter than one reporting
        // interval would otherwise never be checked at all.
        if (!Thresholds.IsEmpty) Thresholds.Check(sessionStats.Duration, scenarioStats, metrics, isFinal: true);

        return sessionStats with
        {
            PluginStats = pluginStats,
            Metrics = metrics,
            Thresholds = Thresholds.GetResults()
        };
    }

    private HintResult[] GetHints(SessionStats finalStats)
    {
        if (!_sessionArgs.EnableHintsAnalyzer) return [];

        return
        [
            .. HintsAnalyzer.AnalyzeSessionStats(finalStats),
            .. WorkerPlugins.GetHints(_dep.WorkerPlugins)
        ];
    }

    public void Dispose() => _buildRealtimeStatsTimer.Dispose();
}
