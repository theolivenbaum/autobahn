using Microsoft.Extensions.Logging;
using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Scheduler;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Internal.Infra;
using Autobahn.Stats;

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
        var initResult = await StartInit(sessionArgs).ConfigureAwait(false);
        if (initResult.IsError) return Result<SessionResult>.Fail(initResult.Error);

        var initializedScenarios = initResult.Value;

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
        await StartBombing(bombingSchedulers, reportingManager).ConfigureAwait(false);

        _dep.LogInfo("Calculating final statistics...");
        var sessionResult = await reportingManager.GetSessionResult(GetCurrentHostInfo()).ConfigureAwait(false);

        return Result<SessionResult>.Ok(sessionResult);
    }

    public Task<Result<List<RuntimeScenario>>> StartInit(SessionArgs sessionArgs)
    {
        _stopped = false;
        _currentOperation = OperationType.Init;

        TestHostConsole.PrintContextInfo(_dep, sessionArgs);
        _dep.LogInfo("Starting init...");

        return TestHostConsole.DisplayStatus(_dep, "Initializing scenarios...", async consoleStatus =>
        {
            var baseContext = ContextResolver.CreateBaseContext(sessionArgs.TestInfo, GetCurrentHostInfo, _dep.Logger);

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
        await Task.WhenAll(schedulers.Select(x => x.StopAsync())).ConfigureAwait(false);

        _currentOperation = OperationType.None;
    }

    public async Task StartBombing(List<ScenarioScheduler> schedulers, IReportingManager reportingManager)
    {
        _stopped = false;
        _currentOperation = OperationType.Bombing;
        _currentSchedulers = schedulers;

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
            await Task.Delay(1_000).ConfigureAwait(false);
        }
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

        await Task.WhenAll(_currentSchedulers.Select(x => x.StopAsync())).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(reason)) _dep.LogWarn($"Stopping test early: {reason}");
        else _dep.LogInfo("Stopping scenarios...");

        await _currentBombingTask.ConfigureAwait(false);

        // The schedulers hold the scenarios that know their executed duration; the target list
        // still holds the planned ones.
        var finishedScenarios = _currentSchedulers.Select(x => x.Scenario).ToList();
        var scenarios = ScenarioFactory.UpdateExecutedDuration(_targetScenarios, finishedScenarios);

        await TestHostConsole.DisplayStatus(_dep, "Cleaning scenarios...", async consoleStatus =>
        {
            var baseContext = ContextResolver.CreateBaseContext(_sessionArgs.TestInfo, GetCurrentHostInfo, _dep.Logger);
            await TestHostScenario.CleanScenarios(_dep, consoleStatus, baseContext, scenarios).ConfigureAwait(false);

            _stopped = true;
            _currentOperation = OperationType.None;
            return true;
        }).ConfigureAwait(false);
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
                GetHostInfo = GetCurrentHostInfo
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
