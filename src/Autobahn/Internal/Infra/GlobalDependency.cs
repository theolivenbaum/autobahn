using Autobahn.Configuration;
using Autobahn.Internal.Domain.Metrics;
using Autobahn.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Autobahn.Internal.Infra;

/// <summary>The ambient services a session hands to everything inside it.</summary>
internal interface IGlobalDependency
{
    ApplicationType ApplicationType { get; }
    AutobahnConfig? Config { get; }
    IConfiguration? InfraConfig { get; }
    ILogger Logger { get; }
    ILogger ConsoleLogger { get; }
    IReadOnlyList<IWorkerPlugin> WorkerPlugins { get; }

    /// <summary>This run's metrics: the registry user code writes to, and the runtime collector.</summary>
    MetricsManager Metrics { get; }
}

internal sealed class GlobalDependency : IGlobalDependency, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILoggerFactory _consoleLoggerFactory;

    public GlobalDependency(
        ApplicationType applicationType,
        LoggerInitSettings logSettings,
        AutobahnContext context)
    {
        ApplicationType = applicationType;
        Config = context.Config;
        InfraConfig = context.InfraConfig;
        WorkerPlugins = context.WorkerPlugins;

        _consoleLoggerFactory = LoggerBuilder.CreateConsoleLoggerFactory();
        _loggerFactory = LoggerBuilder.CreateLoggerFactory(logSettings, context);

        ConsoleLogger = _consoleLoggerFactory.CreateLogger("Autobahn");
        Logger = _loggerFactory.CreateLogger("Autobahn");

        Metrics = new MetricsManager(Logger, context.EnableRuntimeMetrics);
    }

    public ApplicationType ApplicationType { get; }
    public AutobahnConfig? Config { get; }
    public IConfiguration? InfraConfig { get; }
    public ILogger Logger { get; }
    public ILogger ConsoleLogger { get; }
    public IReadOnlyList<IWorkerPlugin> WorkerPlugins { get; }
    public MetricsManager Metrics { get; }

    public void Dispose()
    {
        Metrics.Dispose();
        _loggerFactory.Dispose();
        _consoleLoggerFactory.Dispose();
    }
}
