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
            response = await run(ctx).ConfigureAwait(false);
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
}
