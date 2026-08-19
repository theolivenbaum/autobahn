using System.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Autobahn.Plugins;

namespace Autobahn.Tests;

[NotInParallel]
public class PluginTests
{
    private sealed class RecordingPlugin(List<string> invocationOrder) : IWorkerPlugin
    {
        public string PluginName => "TestPlugin";

        public Task Init(IBaseContext context, IConfiguration infraConfig)
        {
            invocationOrder.Add("init");
            return Task.CompletedTask;
        }

        public Task Start()
        {
            invocationOrder.Add("start");
            return Task.CompletedTask;
        }

        public Task<DataSet> GetStats(Stats.SessionStats stats)
        {
            invocationOrder.Add("get_stats");
            return Task.FromResult(new DataSet());
        }

        public string[] GetHints()
        {
            invocationOrder.Add("get_hints");
            return [];
        }

        public Task Stop()
        {
            invocationOrder.Add("stop");
            return Task.CompletedTask;
        }

        public void Dispose() => invocationOrder.Add("dispose");
    }

    private sealed class LambdaPlugin : IWorkerPlugin
    {
        public required Func<IBaseContext, IConfiguration, Task> OnInit { get; init; }
        public required Func<Task<DataSet>> OnGetStats { get; init; }

        public string PluginName => "TestPlugin";
        public Task Init(IBaseContext context, IConfiguration infraConfig) => OnInit(context, infraConfig);
        public Task Start() => Task.CompletedTask;
        public Task<DataSet> GetStats(Stats.SessionStats stats) => OnGetStats();
        public string[] GetHints() => [];
        public Task Stop() => Task.CompletedTask;
        public void Dispose() { }
    }

    [Test]
    [Category("slow")]
    public async Task A_plugin_is_started_and_stopped_once_per_phase()
    {
        var invocationOrder = new List<string>();

        AutobahnRunner
            .RegisterScenarios(PluginTestHelper.CreateScenarios())
            .WithWorkerPlugins(new RecordingPlugin(invocationOrder))
            .EnableHintsAnalyzer(true)
            .WithReportingInterval(Time.Seconds(5))
            .WithoutReports()
            .Run();

        await Assert.That(invocationOrder)
            .IsEquivalentTo(new List<string> { "init", "start", "stop", "start", "stop", "get_stats", "get_hints", "dispose" });
    }

    [Test]
    public async Task A_plugin_receives_the_infra_config()
    {
        IConfiguration? pluginConfig = null;

        var plugin = new LambdaPlugin
        {
            OnInit = (_, infraConfig) =>
            {
                pluginConfig = infraConfig;
                return Task.CompletedTask;
            },
            OnGetStats = () => Task.FromResult(new DataSet())
        };

        AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("1", _ => Task.FromResult<IResponse>(Response.Ok()))
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(1))))
            .LoadInfraConfig("Assets/Configuration/infra_config.json")
            .WithWorkerPlugins(plugin)
            .WithoutReports()
            .Run();

        await Assert.That(pluginConfig).IsNotNull();
        await Assert.That(pluginConfig!.GetSection("PingPlugin:Timeout").Value).IsEqualTo("500");
    }

    [Test]
    [Category("slow")]
    public async Task A_plugin_that_takes_too_long_to_report_is_given_up_on()
    {
        var logs = new InMemoryLoggerProvider();

        var plugin = new LambdaPlugin
        {
            OnInit = (_, _) => Task.CompletedTask,
            OnGetStats = async () =>
            {
                // Longer than the plugin-stats timeout.
                await Task.Delay(Time.Seconds(10));
                return new DataSet();
            }
        };

        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("1", _ => Task.FromResult<IResponse>(Response.Ok()))
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(10))))
            .WithLogging(builder => builder.AddProvider(logs))
            .WithWorkerPlugins(plugin)
            .WithoutReports()
            .Run("disposeLogger=false");

        await Assert.That(stats.PluginStats).IsEmpty();
        await Assert.That(logs.HasMessage("Getting plugin stats failed with the timeout error")).IsTrue();
    }

    [Test]
    public async Task A_plugin_that_throws_while_reporting_does_not_take_the_run_down()
    {
        var logs = new InMemoryLoggerProvider();

        var plugin = new LambdaPlugin
        {
            OnInit = (_, _) => Task.CompletedTask,
            OnGetStats = () => throw new InvalidOperationException("test exception")
        };

        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("1", _ => Task.FromResult<IResponse>(Response.Ok()))
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(2))))
            .WithLogging(builder => builder.AddProvider(logs))
            .WithWorkerPlugins(plugin)
            .WithoutReports()
            .Run("disposeLogger=false");

        await Assert.That(stats.PluginStats).IsEmpty();
        await Assert.That(logs.HasMessage("Getting plugin stats failed")).IsTrue();
    }

    [Test]
    public async Task Plugin_stats_reach_the_reports()
    {
        var plugin = new LambdaPlugin
        {
            OnInit = (_, _) => Task.CompletedTask,
            OnGetStats = () => Task.FromResult(PluginStatisticsHelper.CreatePluginStats())
        };

        const string folder = "./reports-plugin-stats";
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);

        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("1", _ => Task.FromResult<IResponse>(Response.Ok()))
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(2))))
            .WithWorkerPlugins(plugin)
            .WithReportFolder(folder)
            .Run();

        await Assert.That(stats.PluginStats.Length).IsEqualTo(1);

        var txt = stats.ReportFiles.Single(x => x.ReportFormat == Stats.ReportFormat.Txt).ReportContent;
        var html = stats.ReportFiles.Single(x => x.ReportFormat == Stats.ReportFormat.Html).ReportContent;

        await Assert.That(txt).Contains("PluginStatistics1Table");
        await Assert.That(txt).Contains("PluginStatistics1RowKey1");
        await Assert.That(html).Contains("PluginStatistics1Table");
    }
}
