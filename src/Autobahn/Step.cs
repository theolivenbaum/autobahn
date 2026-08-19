using System.Runtime.CompilerServices;
using Autobahn.Internal;
using Autobahn.Internal.Domain;

namespace Autobahn;

/// <summary>
/// A single user action inside a scenario - login, search, checkout - measured separately.
/// A scenario that does not need splitting up does not need steps at all.
/// </summary>
public static class Step
{
    /// <summary>Runs and measures one step.</summary>
    /// <param name="name">Any name except the reserved "global information".</param>
    /// <param name="context">The running scenario's context.</param>
    /// <param name="run">The user action to invoke and measure.</param>
    /// <param name="timeout">
    /// Gives up on the step after this long and records it as a timeout rather than as a
    /// generic error. Null lets it run as long as the iteration allows.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async Task<Response<T>> Run<T>(
        string name, IScenarioContext context, Func<Task<Response<T>>> run, TimeSpan? timeout = null)
    {
        if (name == Constants.ScenarioGlobalInfo)
        {
            context.StopCurrentTest(
                $"The '{Constants.ScenarioGlobalInfo}' is a reserved name that can't be used for the step name. "
                + "Please use any different name.");
        }

        var ctx = (ScenarioExecutionContext)context;
        var response = await StepExecution.Measure(name, ctx, run, timeout).ConfigureAwait(false);

        // Restarting the iteration is the scenario's default: one failed step usually means the
        // rest of the flow would be measuring nonsense.
        if (response.IsError && ctx.RestartIterationOnFail) throw new RestartScenarioIterationException();

        return response;
    }
}
