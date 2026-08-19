using Microsoft.Extensions.Logging;
using ZLogger;

namespace Autobahn.Internal.Domain;

/// <summary>Times one step and publishes the measurement, whatever the step does.</summary>
internal static class StepExecution
{
    public static async Task<Response<T>> Measure<T>(
        string name, ScenarioExecutionContext ctx, Func<Task<Response<T>>> run)
    {
        var timeBucket = ctx.CurrentTimeBucket;
        var startTime = ctx.Timer.Elapsed;

        Response<T> response;

        try
        {
            response = await run().ConfigureAwait(false);
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
}
