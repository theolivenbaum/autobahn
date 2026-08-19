using Microsoft.Extensions.Logging;
using Autobahn.Metrics;
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

    /// <summary>
    /// This run's metrics. Every copy of every scenario writes to the same registry, so a
    /// metric is a series over the run rather than over one copy - name it accordingly.
    /// </summary>
    IMetricRegistry Metrics { get; }

    /// <summary>How many iterations this copy has started, counting the current one.</summary>
    int InvocationNumber { get; }

    /// <summary>Per-iteration scratch space. Cleared before every iteration.</summary>
    Dictionary<string, object> Data { get; }

    /// <summary>
    /// Cancelled when the scenario stops, and when this iteration outruns its timeout.
    /// Pass it to whatever the iteration calls so a cancelled iteration actually stops
    /// working rather than being abandoned still running.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Stops one scenario early. Other scenarios keep running.</summary>
    void StopScenario(string scenarioName, string reason);

    /// <summary>Stops the whole test early.</summary>
    void StopCurrentTest(string reason);
}
