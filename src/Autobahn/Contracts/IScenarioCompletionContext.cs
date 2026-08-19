using Autobahn.Stats;
using Microsoft.Extensions.Logging;

namespace Autobahn;

/// <summary>
/// What a scenario's completion hook receives: that scenario's final numbers, once it has
/// stopped and before the session moves on.
/// </summary>
/// <remarks>
/// The place to push a result somewhere, tear down a fixture keyed to this scenario, or
/// decide a build has failed - without wrapping the whole runner.
/// </remarks>
public interface IScenarioCompletionContext
{
    TestInfo TestInfo { get; }
    ScenarioInfo ScenarioInfo { get; }
    ILogger Logger { get; }

    /// <summary>This scenario's final statistics.</summary>
    ScenarioStats Stats { get; }
}
