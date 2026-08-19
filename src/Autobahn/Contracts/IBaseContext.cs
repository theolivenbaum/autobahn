using Autobahn.Metrics;
using Autobahn.Stats;
using Microsoft.Extensions.Logging;

namespace Autobahn;

/// <summary>What a plugin sees of the session it is attached to.</summary>
public interface IBaseContext
{
    TestInfo TestInfo { get; }
    ILogger Logger { get; }
    HostInfo GetHostInfo();

    /// <summary>This run's metrics. Registering the same name twice hands back the same metric.</summary>
    IMetricRegistry Metrics { get; }
}
