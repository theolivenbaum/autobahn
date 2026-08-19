using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Scheduler;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Internal.Infra;
using Autobahn.Stats;
using Microsoft.Extensions.Logging;

namespace Autobahn.Internal.Services.TestHost;

/// <summary>
/// Drives one session: init, optional warm-up, bombing, clean, report.
/// </summary>
internal sealed class TestHost : IDisposable
{
    private readonly IGlobalDependency _dep;
    private readonly IReadOnlyList<RuntimeScenario> _regScenarios;
    private readonly List<ScenarioStatsActor> _statsActors = [];

    // Every scheduler ever created, not just the current phase's: the warm-up schedulers
    // are replaced by the bombing ones and still hold a timer and a cancellation source.
    private readonly List<ScenarioScheduler> _allSchedulers = [];

    private List<ScenarioScheduler> _currentSchedulers = [];

    // Sticky, unlike _stopped: a stop asked for from outside has to survive the phase
    // transitions that reset _stopped, or a token cancelled during init is simply forgotten.
    private volatile string? _externalStopReason;

    private bool _stopped;
    private bool _disposed;
    private List<RuntimeScenario> _targetScenarios = [];
    private SessionArgs _sessionArgs = SessionArgs.Empty;
    private OperationType _currentOperation = OperationType.None;
    private HostInfo _hostInfo;
    private Task _currentBombingTask = Task.CompletedTask;

    public TestHost(IGlobalDependency dep, IReadOnlyList<RuntimeScenario> regScenarios)
    {
        _dep = dep;
        _regScenarios = regScenarios;
        _hostInfo = HostInfoProvider.Init() with { CurrentOperation = _currentOperation };
    }

    /// <summary>The scenarios that were initialized and run, with their load plans resolved.</summary>
    public IReadOnlyList<RuntimeScenario> TargetScenarios => _targetScenarios;

    private HostInfo GetCurrentHostInfo()
    {
        if (_hostInfo.CurrentOperation == _currentOperation) return _hostInfo;

        _hostInfo = _hostInfo with { CurrentOperation = _currentOperation };
        return _hostInfo;
    }

    public async Task<Result<SessionResult>> RunSession(SessionArgs sessionArgs)
    {
        using var externalStop = RegisterExternalStop(sessionArgs);

        var initResult = await StartInit(sessionArgs).ConfigureAwait(false);
        if (initResult.IsError) return Result<SessionResult>.Fail(initResult.Error);

        var initializedScenarios = initResult.Value;

        await NotifySessionStart(sessionArgs).ConfigureAwait(false);

        var warmUpScenarios = ScenarioFactory.GetScenariosForWarmUp(initializedScenarios);
        if (warmUpScenarios.Count > 0)
        {
            var warmUpSchedulers = CreateScenarioSchedulers(warmUpScenarios, ScenarioOperation.WarmUp);
            using var warmUpReporting = new ReportingManager(_dep, warmUpSchedulers, sessionArgs);
            await StartWarmUp(warmUpScenarios, warmUpSchedulers, warmUpReporting).ConfigureAwait(false);
        }

        var bombingSchedulers = CreateScenarioSchedulers(initializedScenarios, ScenarioOperation.Bombing);

        if (bombingSchedulers.Count == 0)
        {
            var emptyResult = SessionResult.Empty;
            return Result<SessionResult>.Ok(emptyResult with
            {
                FinalStats = emptyResult.FinalStats with { TestInfo = sessionArgs.TestInfo }
            });
        }

        using var reportingManager = new ReportingManager(_dep, bombingSchedulers, sessionArgs);

        // A threshold with an abort policy is the difference between a report saying a service
        // was down and not hammering a service that is already down.
        reportingManager.OnThresholdAbort = reason => _ = StopTest(reason);

        await StartBombing(bombingSchedulers, reportingManager).ConfigureAwait(false);

        _dep.LogInfo("Calculating final statistics...");
        var sessionResult = await reportingManager.GetSessionResult(GetCurrentHostInfo()).ConfigureAwait(false);

        var completionContext = ContextResolver.CreateBaseContext(
            sessionArgs.TestInfo, GetCurrentHostInfo, _dep.Logger, _dep.Metrics.Registry);

        await TestHostScenario
            .RunCompletionHooks(_dep, completionContext, _targetScenarios, sessionResult.FinalStats)
            .ConfigureAwait(false);

        return Result<SessionResult>.Ok(sessionResult);
    }

    /// <summary>
    /// Tells whoever asked what this run turned out to be, once the scenarios are initialized
    /// and before any load is generated.
    /// </summary>
    /// <remarks>
    /// After init rather than before it, because the plans are only resolved by then: a
    /// weighted scenario's simulations are rescaled during initialization, and a descriptor
    /// taken any earlier would describe a plan that is not the one about to run.
    ///
    /// Awaited, unlike the interval observer: nothing is being measured yet, so there is no
    /// timing for it to distort, and a watcher that has to be ready before the first interval
    /// closes needs the chance to be. A failure is logged and the run proceeds - being watched
    /// is never a reason to lose a test.
    /// </remarks>
    private async Task NotifySessionStart(SessionArgs sessionArgs)
    {
        if (sessionArgs.OnSessionStart is not { } observe) return;

        try
        {
            await observe(new SessionStartInfo
            {
                TestInfo = sessionArgs.TestInfo,
                HostInfo = GetCurrentHostInfo(),
                ReportingInterval = sessionArgs.ReportingInterval,
                ReportFolder = sessionArgs.ReportFolder,
                EffectiveSettings = sessionArgs.EffectiveSettings,
                Thresholds = sessionArgs.Thresholds,
                Scenarios = _targetScenarios
                    .Select(scn => new ScenarioStartInfo
                    {
                        ScenarioName = scn.ScenarioName,
                        LoadSimulations = scn.LoadSimulations.Select(x => x.Value).ToArray(),
                        // A counted segment has no length to promise, and a progress bar that
                        // invents one is worse than no progress bar.
                        PlannedDuration = scn.HasCountedSimulations ? null : scn.PlanedDuration,
                        WarmUpDuration = scn.WarmUpDuration,
                        MaxCopies = scn.MaxCopiesCount,
                        Weight = scn.Weight
                    })
                    .ToArray()
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dep.LogError($"The session-start observer failed: {ex.Message}");
        }
    }

    public Task<Result<List<RuntimeScenario>>> StartInit(SessionArgs sessionArgs)
    {
        _stopped = false;
        _currentOperation = OperationType.Init;

        TestHostConsole.PrintContextInfo(_dep, sessionArgs);
        if (sessionArgs.ShowEffectiveConfig) TestHostConsole.PrintEffectiveConfig(_dep, sessionArgs);
        _dep.LogInfo("Starting init...");

        return TestHostConsole.DisplayStatus(_dep, "Initializing scenarios...", async consoleStatus =>
        {
            var baseContext = ContextResolver.CreateBaseContext(
                sessionArgs.TestInfo, GetCurrentHostInfo, _dep.Logger, _dep.Metrics.Registry);

            var pluginsInit = await WorkerPlugins.Init(_dep, baseContext).ConfigureAwait(false);
            if (pluginsInit.IsError)
            {
                _currentOperation = OperationType.Error;
                return Result<List<RuntimeScenario>>.Fail(pluginsInit.Error);
            }

            var initResult = await TestHostScenario
                .InitScenarios(_dep, consoleStatus, baseContext, sessionArgs, _regScenarios)
                .ConfigureAwait(false);

            if (initResult.IsError)
            {
                _currentOperation = OperationType.Error;
                return initResult;
            }

            _dep.LogInfo("Init finished");

            _targetScenarios = initResult.Value;
            _sessionArgs = sessionArgs;
            _currentOperation = OperationType.None;

            return Result<List<RuntimeScenario>>.Ok(_targetScenarios);
        });
    }

    public async Task StartWarmUp(
        IReadOnlyList<RuntimeScenario> scenarios,
        List<ScenarioScheduler> schedulers,
        IReportingManager reportingManager)
    {
        _stopped = false;
        _currentOperation = OperationType.WarmUp;
        _currentSchedulers = schedulers;

        _dep.LogInfo("Starting warm up...");
        TestHostConsole.PrintWarmUpScenarios(_dep, scenarios);

        _currentBombingTask = StartScenarios(isWarmUp: true, schedulers, reportingManager);
        await _currentBombingTask.ConfigureAwait(false);
        await StopSchedulers(schedulers).ConfigureAwait(false);

        _currentOperation = OperationType.None;
    }

    public async Task StartBombing(List<ScenarioScheduler> schedulers, IReportingManager reportingManager)
    {
        _stopped = false;
        _currentOperation = OperationType.Bombing;
        _currentSchedulers = schedulers;

        // The metrics start clean at the bombing phase, so the series they report cover the
        // same window every other number in the report does. Warm-up is not part of it.
        _dep.Metrics.Reset();
        _dep.Metrics.Start();

        _dep.LogInfo("Starting bombing...");

        _currentBombingTask = StartScenarios(isWarmUp: false, schedulers, reportingManager);
        await _currentBombingTask.ConfigureAwait(false);
        await StopTest().ConfigureAwait(false);

        _currentOperation = OperationType.Complete;
    }

    private async Task StartScenarios(
        bool isWarmUp,
        IReadOnlyList<ScenarioScheduler> schedulers,
        IReportingManager reportingManager)
    {
        WorkerPlugins.Start(_dep);

        using var consoleCancelToken = new CancellationTokenSource();
        var maxDuration = ScenarioFactory.GetMaxDuration(schedulers.Select(x => x.Scenario));

        TestHostConsole.LiveStatusTable.Display(_dep, consoleCancelToken.Token, isWarmUp, schedulers);

        var bombingTask = Task.WhenAll(schedulers.Select(x => x.Start(consoleCancelToken.Token)));

        // A stop asked for before this phase began - a token already cancelled when Run was
        // called, or Ctrl+C during init - applies to the schedulers that did not exist yet.
        if (_externalStopReason is not null)
            await Task.WhenAll(schedulers.Select(x => x.StopAsync())).ConfigureAwait(false);

        // "Stop forcibly" means the run ends when the plan says so, even if the generator is
        // lagging behind and still has iterations in flight.
        if (_sessionArgs.EnableStopTestForcibly) consoleCancelToken.CancelAfter(maxDuration);

        _ = reportingManager.Start();
        await bombingTask.ConfigureAwait(false);
        await consoleCancelToken.CancelAsync().ConfigureAwait(false);

        await reportingManager.Stop().ConfigureAwait(false);
        await WorkerPlugins.Stop(_dep).ConfigureAwait(false);

        if (isWarmUp)
        {
            GC.Collect();
            await Task.Delay(Constants.WarmUpSettleDelay, _dep.Time).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wires the two ways a run can be ended from outside it - a cancellation token the caller
    /// holds, and Ctrl+C - onto the same ordinary early stop, so a cancelled run still winds
    /// its scenarios down, still calculates its statistics and still writes its reports.
    /// </summary>
    /// <remarks>
    /// Ctrl+C is only intercepted once. Pressing it again goes to the runtime's own handler and
    /// kills the process, which is the escape hatch when a scenario refuses to stop.
    /// </remarks>
    private IDisposable RegisterExternalStop(SessionArgs sessionArgs)
    {
        var registrations = new List<IDisposable>(2);

        if (sessionArgs.CancellationToken.CanBeCanceled)
        {
            registrations.Add(sessionArgs.CancellationToken.Register(
                () => RequestExternalStop(Constants.StopReasonCancelled)));
        }

        if (sessionArgs.EnableCancelKeyPress && _dep.ApplicationType == ApplicationType.Console)
        {
            var handled = 0;

            void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
            {
                if (Interlocked.Exchange(ref handled, 1) == 1) return;

                e.Cancel = true;
                _dep.LogWarn(Constants.StopReasonCtrlC);
                RequestExternalStop(Constants.StopReasonCtrlC);
            }

            Console.CancelKeyPress += OnCancelKeyPress;
            registrations.Add(new Unregister(() => Console.CancelKeyPress -= OnCancelKeyPress));
        }

        return new Unregister(() =>
        {
            foreach (var registration in registrations) registration.Dispose();
        });
    }

    private void RequestExternalStop(string reason)
    {
        _externalStopReason = reason;
        _ = StopTest(reason);
    }

    private sealed class Unregister(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    public void ExecStopCommand(StopCommand command)
    {
        switch (command)
        {
            case StopCommand.StopScenario stop:
                StopScenario(stop.ScenarioName, stop.Reason);
                break;

            case StopCommand.StopTest stop:
                _ = StopTest(stop.Reason);
                break;
        }
    }

    public void StopScenario(string scenarioName, string reason)
    {
        var scheduler = _currentSchedulers
            .FirstOrDefault(sch => sch.Working && sch.Scenario.ScenarioName == scenarioName);

        if (scheduler is null) return;

        _ = scheduler.StopAsync();

        _dep.LogWarn($"Stopping scenario early: {scheduler.Scenario.ScenarioName}, reason: {reason}");
    }

    public async Task StopTest(string reason = "")
    {
        if (_currentOperation == OperationType.Stop || _stopped) return;

        _currentOperation = OperationType.Stop;

        await StopSchedulers(_currentSchedulers).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(reason)) _dep.LogWarn($"Stopping test early: {reason}");
        else _dep.LogInfo("Stopping scenarios...");

        await _currentBombingTask.ConfigureAwait(false);

        // The schedulers hold the scenarios that know their executed duration; the target list
        // still holds the planned ones.
        var finishedScenarios = _currentSchedulers.Select(x => x.Scenario).ToList();
        var scenarios = ScenarioFactory.UpdateExecutedDuration(_targetScenarios, finishedScenarios);

        await TestHostConsole.DisplayStatus(_dep, "Cleaning scenarios...", async consoleStatus =>
        {
            var baseContext = ContextResolver.CreateBaseContext(
                _sessionArgs.TestInfo, GetCurrentHostInfo, _dep.Logger, _dep.Metrics.Registry);
            await TestHostScenario.CleanScenarios(_dep, consoleStatus, baseContext, scenarios).ConfigureAwait(false);

            _stopped = true;
            _currentOperation = OperationType.None;
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops every scheduler and says how many iterations were abandoned mid-flight, because
    /// a hole in the numbers is something the operator should be told about rather than left
    /// to infer from a count that does not add up.
    /// </summary>
    private async Task StopSchedulers(IReadOnlyList<ScenarioScheduler> schedulers)
    {
        var results = await Task.WhenAll(schedulers.Select(x => x.StopAsync())).ConfigureAwait(false);

        for (var i = 0; i < results.Length; i++)
        {
            if (results[i].AbandonedIterations == 0) continue;

            _dep.LogWarn(
                $"Scenario '{schedulers[i].Scenario.ScenarioName}' abandoned "
                + $"{results[i].AbandonedIterations} iteration(s) that were still running when its completion "
                + "timeout expired. They are not counted in the results.");
        }
    }

    public List<ScenarioScheduler> CreateScenarioSchedulers(
        IReadOnlyList<RuntimeScenario> scenarios, ScenarioOperation operation)
    {
        foreach (var scheduler in _currentSchedulers) _ = scheduler.StopAsync();

        return ScenarioFactory.GetScenariosForBombing(scenarios).Select(CreateScheduler).ToList();

        ScenarioScheduler CreateScheduler(RuntimeScenario scn)
        {
            var statsActor = new ScenarioStatsActor(_dep.Logger, scn, _sessionArgs.ReportingInterval);
            _statsActors.Add(statsActor);

            var scnDep = new ScenarioContextArgs
            {
                Logger = _dep.Logger,
                Scenario = scn,
                ScenarioCancellationToken = new CancellationTokenSource(),
                ScenarioOperation = operation,
                ScenarioStatsActor = statsActor,
                ExecStopCommand = ExecStopCommand,
                TestInfo = _sessionArgs.TestInfo,
                GetHostInfo = GetCurrentHostInfo,
                Metrics = _dep.Metrics.Registry,
                Time = _dep.Time
            };

            var scheduler = new ScenarioScheduler(scnDep);
            _allSchedulers.Add(scheduler);
            return scheduler;
        }
    }

    public void Destroy()
    {
        if (_disposed) return;
        _disposed = true;

        StopTest().GetAwaiter().GetResult();

        foreach (var scheduler in _allSchedulers) scheduler.Dispose();

        foreach (var statsActor in _statsActors)
            statsActor.DisposeAsync().AsTask().GetAwaiter().GetResult();

        foreach (var plugin in _dep.WorkerPlugins) plugin.Dispose();
    }

    public void Dispose() => Destroy();
}
