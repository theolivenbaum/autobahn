using Microsoft.Extensions.Logging;
using ZLogger;

namespace Autobahn.Internal.Domain;

/// <summary>Times one step and publishes the measurement, whatever the step does.</summary>
internal static class StepExecution
{
    public static async Task<Response<T>> Measure<T>(
        string name, ScenarioExecutionContext ctx, Func<Task<Response<T>>> run, TimeSpan? timeout = null)
    {
        var timeBucket = ctx.CurrentTimeBucket;
        var startTime = ctx.Timer.Elapsed;

        Response<T> response;

        try
        {
            response = timeout is { } stepTimeout
                ? await RunWithTimeout(run, stepTimeout).ConfigureAwait(false)
                : await run().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            response = ResponseInternal.FailTimeout<T>();
        }
        catch (Exception ex)
        {
            ctx.Logger.ZLogError(
                $"Unhandled exception for Scenario: {ctx.ScenarioInfo.ScenarioName}, Step: {name}, error: {ex}");

            response = ResponseInternal.FailUnhandled<T>(ex);
        }

        var latency = ctx.Timer.Elapsed - startTime;
        ctx.StatsActor.AddMeasurement(new Measurement(name, response, timeBucket, latency));

        return response;
    }

    /// <summary>
    /// Runs the step, giving up on it once it outruns its timeout. Same reasoning as the
    /// iteration timeout: cancelling asks, not waiting enforces.
    /// </summary>
    private static async Task<Response<T>> RunWithTimeout<T>(Func<Task<Response<T>>> run, TimeSpan timeout)
    {
        var runTask = run();

        var finished = await Task.WhenAny(runTask, Task.Delay(timeout, CancellationToken.None)).ConfigureAwait(false);

        if (ReferenceEquals(finished, runTask)) return await runTask.ConfigureAwait(false);

        _ = runTask.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return ResponseInternal.FailTimeout<T>();
    }
}
