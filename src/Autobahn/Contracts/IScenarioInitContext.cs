using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Autobahn.Metrics;
using Autobahn.Stats;

namespace Autobahn;

/// <summary>What a scenario's init and clean functions receive.</summary>
public interface IScenarioInitContext
{
    TestInfo TestInfo { get; }
    ScenarioInfo ScenarioInfo { get; }
    HostInfo HostInfo { get; }

    /// <summary>This scenario's CustomSettings section from the JSON config, if any.</summary>
    IConfiguration CustomSettings { get; }

    ILogger Logger { get; }

    /// <summary>This run's metrics. The natural place to register the ones a scenario writes to.</summary>
    IMetricRegistry Metrics { get; }
}
