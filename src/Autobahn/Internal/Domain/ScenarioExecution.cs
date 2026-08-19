using Microsoft.Extensions.Logging;
using ZLogger;

namespace Autobahn.Internal.Domain;

/// <summary>Times one whole scenario iteration and publishes the measurement.</summary>
internal static class ScenarioExecution
{
    public static async Task Measure(
        string name,
        ScenarioExecutionContext ctx,
        TimeSpan timeBucket,
        Func<IScenarioContext, Task<IResponse>> run)
    {
        var startTime = ctx.Timer.Elapsed;
        IResponse response;

        try
        {
            response = ctx.IterationTimeout is { } timeout
                ? await RunWithTimeout(ctx, run, timeout).ConfigureAwait(false)
                : await run(ctx).ConfigureAwait(false);
        }
        catch (RestartScenarioIterationException)
        {
            // A step failed and the scenario asked for the iteration to restart. The iteration
            // is counted as failed, but without a status code or a message: the step that
            // actually failed already recorded both.
            response = ResponseInternal.FailEmpty<object>();
        }
        catch (OperationCanceledException)
        {
            response = ResponseInternal.FailTimeout<object>();
        }
        catch (Exception ex)
        {
            ctx.Logger.ZLogError($"Unhandled exception for Scenario: {ctx.ScenarioInfo.ScenarioName}, error: {ex}");
            response = ResponseInternal.FailUnhandled<object>(ex);
        }

        var latency = ctx.Timer.Elapsed - startTime;
        ctx.StatsActor.AddMeasurement(new Measurement(name, response, timeBucket, latency));
    }

    /// <summary>
    /// Runs the iteration, giving up on it once it outruns its timeout.
    /// </summary>
    /// <remarks>
    /// Cancelling the token asks user code to stop, but nothing can force it to: an iteration
    /// that ignores its token keeps running. So the timeout is enforced by not waiting rather
    /// than by cancelling - the measurement is recorded as a timeout and the actor moves on,
    /// while the abandoned task's eventual failure is observed so it cannot resurface as an
    /// unobserved exception.
    /// </remarks>
    private static async Task<IResponse> RunWithTimeout(
        ScenarioExecutionContext ctx, Func<IScenarioContext, Task<IResponse>> run, TimeSpan timeout)
    {
        var runTask = run(ctx);

        var finished = await Task.WhenAny(runTask, Task.Delay(timeout, ctx.Time, CancellationToken.None)).ConfigureAwait(false);

        if (ReferenceEquals(finished, runTask)) return await runTask.ConfigureAwait(false);

        ctx.CancelIteration();
        Observe(runTask);

        return ResponseInternal.FailIterationTimeout<object>();
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
