using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ZLogger;
using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Scheduler;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Internal.Domain.Thresholds;
using Autobahn.Internal.Infra;
using Autobahn.Stats;

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
    private readonly System.Timers.Timer _buildRealtimeStatsTimer;

    // What the metrics did in each closed interval, keyed the same way the scheduler stats
    // are so the two line up in the timeline.
    private readonly ConcurrentDictionary<TimeSpan, MetricStats[]> _intervalMetrics = new();

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

        _buildRealtimeStatsTimer = new System.Timers.Timer(_reportingInterval.TotalMilliseconds);
        _buildRealtimeStatsTimer.Elapsed += OnElapsed;
    }

    private void OnElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        var duration = _curDuration + _reportingInterval;

        if (duration > _timerMaxDuration)
        {
            _buildRealtimeStatsTimer.Stop();
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
                task => CheckThresholds(duration, task, metrics),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
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

    public async Task Start()
    {
        await Task.Delay(Constants.ReportingManagerStartDelay).ConfigureAwait(false);
        _buildRealtimeStatsTimer.Start();
    }

    public async Task Stop()
    {
        await Task.Delay(Constants.ReportingManagerStartDelay).ConfigureAwait(false);
        _buildRealtimeStatsTimer.Stop();
    }

    public async Task<SessionResult> GetSessionResult(HostInfo hostInfo)
    {
        var history = TimeLineHistory.Create(_schedulers.Select(x => x.AllRealtimeStats), _intervalMetrics);
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

    public void Dispose()
    {
        _buildRealtimeStatsTimer.Elapsed -= OnElapsed;
        _buildRealtimeStatsTimer.Dispose();
    }
}
