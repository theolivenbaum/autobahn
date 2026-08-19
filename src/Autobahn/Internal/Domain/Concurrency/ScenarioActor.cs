using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Autobahn.Internal.Domain.Concurrency;

/// <summary>
/// One virtual user. Prepares an iteration, runs the user's scenario function, reports the
/// measurement, repeats - either once or until asked to stop.
/// </summary>
internal sealed class ScenarioActor : IDisposable
{
    private readonly ILogger _logger;
    private readonly ScenarioContextArgs _scnCtx;
    private readonly RuntimeScenario _scenario;
    private readonly CancellationToken _cancelToken;
    private readonly Stopwatch _timer = new();
    private readonly ScenarioExecutionContext _scenarioCtx;

    private volatile bool _working;
    private volatile bool _shouldStop;

    public ScenarioActor(ScenarioContextArgs scnCtx, ScenarioInfo scenarioInfo)
    {
        _logger = scnCtx.Logger;
        _scnCtx = scnCtx;
        _scenario = scnCtx.Scenario;
        _cancelToken = scnCtx.ScenarioCancellationToken.Token;
        _scenarioCtx = new ScenarioExecutionContext(scnCtx, _timer, scenarioInfo);
        ScenarioInfo = scenarioInfo;

        _timer.Start();
    }

    public ScenarioInfo ScenarioInfo { get; }

    /// <summary>True while an iteration loop is in flight.</summary>
    public bool Working => _working;

    /// <summary>Runs exactly one iteration, after a jittered start inside the injection interval.</summary>
    public Task ExecSteps(TimeSpan injectInterval) => Run(StartDelay(injectInterval), runInfinite: false);

    /// <summary>Runs iterations back to back until asked to stop.</summary>
    public Task RunInfinite(TimeSpan injectInterval) => Run(StartDelay(injectInterval), runInfinite: true);

    public void AskToStop() => _shouldStop = true;

    /// <summary>
    /// Actors injected together are spread across the interval rather than firing in one burst.
    /// </summary>
    private static int StartDelay(TimeSpan injectInterval)
    {
        var maxDelay = (int)injectInterval.TotalMilliseconds;
        return maxDelay <= 0 ? 0 : Random.Shared.Next(0, maxDelay);
    }

    private async Task Run(int startDelayMs, bool runInfinite)
    {
        if (_working)
        {
            // The schedulers only ever hand work to actors that report themselves free, so
            // reaching this means a scheduling bug rather than a user one. Unlike the fork
            // point, the already-working actor's own loop is left alone: clearing the flag
            // from here would corrupt the state of a loop that is still running.
            _logger.ZLogCritical(
                $"Unhandled exception: ExecSteps was invoked for already working actor with Scenario: {_scenario.ScenarioName}");
            return;
        }

        _working = true;
        _shouldStop = false;

        try
        {
            var infiniteRun = true;
            var timeBucket = _scenarioCtx.CurrentTimeBucket;

            if (startDelayMs > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(startDelayMs), _scnCtx.Time).ConfigureAwait(false);

            while (infiniteRun && !_shouldStop && !_cancelToken.IsCancellationRequested)
            {
                // A counted simulation hands out its iterations one at a time. Claiming before
                // running rather than counting after is what makes the total exact.
                var budget = _scnCtx.IterationBudget;
                if (budget is not null && !budget.TryClaim()) break;

                if (_scenario.Run is { } run)
                {
                    _scenarioCtx.PrepareNextIteration();
                    await ScenarioExecution.Measure(Constants.ScenarioGlobalInfo, _scenarioCtx, timeBucket, run)
                        .ConfigureAwait(false);
                }

                budget?.MarkCompleted();

                infiniteRun = runInfinite;
                timeBucket = _scenarioCtx.CurrentTimeBucket;
            }
        }
        finally
        {
            _working = false;
        }
    }

    public void Dispose() => _scenarioCtx.Dispose();
}
