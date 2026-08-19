using System.Data;
using Microsoft.Extensions.Configuration;
using Autobahn.Stats;

namespace Autobahn.Plugins;

/// <summary>
/// A background worker that runs alongside the load test and contributes its own stats
/// and hints to the final report.
/// </summary>
public interface IWorkerPlugin : IDisposable
{
    string PluginName { get; }

    /// <summary>Called once, before the session starts.</summary>
    Task Init(IBaseContext context, IConfiguration infraConfig);

    /// <summary>Called at the start of warm-up and again at the start of bombing.</summary>
    Task Start();

    /// <summary>Called once at the end, with the run's final numbers. Must return within the plugin-stats timeout.</summary>
    Task<DataSet> GetStats(SessionStats stats);

    /// <summary>Post-run advice from this plugin. Only collected when the hints analyzer is enabled.</summary>
    string[] GetHints();

    /// <summary>Called at the end of warm-up and again at the end of bombing.</summary>
    Task Stop();
}
