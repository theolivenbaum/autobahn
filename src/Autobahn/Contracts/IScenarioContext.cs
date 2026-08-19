using Microsoft.Extensions.Logging;
using Autobahn.Stats;

namespace Autobahn;

/// <summary>
/// The execution context of the currently running scenario copy: what it is, where it is,
/// and how to stop the run from inside it.
/// </summary>
public interface IScenarioContext
{
    TestInfo TestInfo { get; }
    ScenarioInfo ScenarioInfo { get; }
    HostInfo HostInfo { get; }
    ILogger Logger { get; }

    /// <summary>How many iterations this copy has started, counting the current one.</summary>
    int InvocationNumber { get; }

    /// <summary>Per-iteration scratch space. Cleared before every iteration.</summary>
    Dictionary<string, object> Data { get; }

    /// <summary>Stops one scenario early. Other scenarios keep running.</summary>
    void StopScenario(string scenarioName, string reason);

    /// <summary>Stops the whole test early.</summary>
    void StopCurrentTest(string reason);
}
