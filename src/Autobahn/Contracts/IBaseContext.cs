using Microsoft.Extensions.Logging;
using Autobahn.Stats;

namespace Autobahn;

/// <summary>What a plugin sees of the session it is attached to.</summary>
public interface IBaseContext
{
    TestInfo TestInfo { get; }
    ILogger Logger { get; }
    HostInfo GetHostInfo();
}
