using Microsoft.Extensions.Logging;
using ZLogger;
using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Scheduler;
using Autobahn.Internal.Domain.Stats;
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

    private TimeSpan _curDuration = TimeSpan.Zero;

    public ReportingManager(IGlobalDependency dep, IReadOnlyList<ScenarioScheduler> schedulers, SessionArgs sessionArgs)
    {
        _dep = dep;
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

        // Fire and forget: the schedulers answer on their own actors, and a slow answer must
        // never hold up the next tick or the run itself.
        _ = Task.WhenAll(_schedulers.Select(x => x.BuildRealtimeStats(duration)));
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
        var history = TimeLineHistory.Create(_schedulers.Select(x => x.AllRealtimeStats));
        var finalStats = await GetFinalStats(hostInfo).ConfigureAwait(false);
        var hints = GetHints(finalStats);

        return new SessionResult { FinalStats = finalStats, TimeLineHistory = history, Hints = hints };
    }

    private async Task<SessionStats> GetFinalStats(HostInfo hostInfo)
    {
        var scenarioStats = await Task.WhenAll(_schedulers.Select(x => x.GetFinalStats())).ConfigureAwait(false);
        var sessionStats = Statistics.CreateSessionStats(_sessionArgs.TestInfo, hostInfo, scenarioStats);
        var pluginStats = await WorkerPlugins.GetStats(_dep, sessionStats).ConfigureAwait(false);

        return sessionStats with { PluginStats = pluginStats };
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
