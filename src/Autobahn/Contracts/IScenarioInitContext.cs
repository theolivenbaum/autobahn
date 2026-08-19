using Autobahn.Metrics;
using Autobahn.Stats;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Autobahn;

/// <summary>What a scenario's init and clean functions receive.</summary>
public interface IScenarioInitContext
{
    TestInfo TestInfo { get; }
    ScenarioInfo ScenarioInfo { get; }
    HostInfo HostInfo { get; }

    /// <summary>
    /// This scenario's CustomSettings section from the JSON config, layered over the run-wide
    /// one. Empty when neither is present.
    /// </summary>
    IConfiguration CustomSettings { get; }

    /// <summary>
    /// The custom settings bound to a type of your own, so a scenario reads
    /// <c>settings.TargetHost</c> rather than <c>CustomSettings["TargetHost"]</c>.
    /// </summary>
    /// <remarks>
    /// Returns a default-constructed <typeparamref name="T"/> when the config has no settings
    /// at all, so a scenario with sensible defaults on its settings type needs no null check.
    /// </remarks>
    T GetCustomSettings<T>() where T : new();

    ILogger Logger { get; }

    /// <summary>This run's metrics. The natural place to register the ones a scenario writes to.</summary>
    IMetricRegistry Metrics { get; }
}
