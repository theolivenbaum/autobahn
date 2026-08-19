using System.Data;
using Autobahn.Internal.Infra;
using Autobahn.Stats;
using Microsoft.Extensions.Configuration;

namespace Autobahn.Internal.Services.TestHost;

/// <summary>Drives the lifecycle of the registered worker plugins.</summary>
internal static class WorkerPlugins
{
    private static readonly IConfiguration EmptyInfraConfig = new ConfigurationBuilder().Build();

    public static async Task<Result<bool>> Init(IGlobalDependency dep, IBaseContext context)
    {
        try
        {
            foreach (var plugin in dep.WorkerPlugins)
            {
                dep.LogInfo($"Start init plugin: {plugin.PluginName}");
                await plugin.Init(context, dep.InfraConfig ?? EmptyInfraConfig).ConfigureAwait(false);
            }

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(new ScenarioError.InitScenarioError(ex));
        }
    }

    public static void Start(IGlobalDependency dep)
    {
        foreach (var plugin in dep.WorkerPlugins)
        {
            try
            {
                _ = plugin.Start();
            }
            catch (Exception ex)
            {
                dep.LogWarn(ex, $"Failed to start plugin: {plugin.PluginName}");
            }
        }
    }

    public static async Task Stop(IGlobalDependency dep)
    {
        foreach (var plugin in dep.WorkerPlugins)
        {
            try
            {
                await plugin.Stop().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                dep.LogWarn(ex, $"Failed to stop plugin: {plugin.PluginName}");
            }
        }
    }

    public static List<HintResult> GetHints(IReadOnlyList<Plugins.IWorkerPlugin> plugins) =>
        plugins
            .SelectMany(plugin => plugin.GetHints().Select(hint => new HintResult
            {
                SourceName = plugin.PluginName,
                SourceType = HintSourceType.WorkerPlugin,
                Hint = hint
            }))
            .ToList();

    /// <summary>
    /// Collects every plugin's stats, giving up on the lot if they take longer than the
    /// plugin-stats timeout: a slow plugin must not hold up the final report.
    /// </summary>
    public static async Task<DataSet[]> GetStats(IGlobalDependency dep, SessionStats stats)
    {
        try
        {
            var pluginStatsTask = Task.WhenAll(dep.WorkerPlugins.Select(plugin => plugin.GetStats(stats)));
            var finishedTask = await Task.WhenAny(pluginStatsTask, Task.Delay(Constants.GetPluginStatsTimeout))
                .ConfigureAwait(false);

            if (ReferenceEquals(finishedTask, pluginStatsTask)) return await pluginStatsTask.ConfigureAwait(false);

            dep.LogWarn("Getting plugin stats failed with the timeout error");
            return [];
        }
        catch (Exception ex)
        {
            dep.LogWarn(ex, "Getting plugin stats failed");
            return [];
        }
    }
}
