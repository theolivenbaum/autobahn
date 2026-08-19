using System.Net.Http;
using System.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Autobahn.Configuration;
using Autobahn.Internal;
using Autobahn.Internal.Json;
using Autobahn.Internal.Services;
using Autobahn.Plugins;
using Autobahn.Stats;

namespace Autobahn;

/// <summary>
/// Registers scenarios and runs them. Everything about a session - reporting, configuration,
/// logging, plugins - is configured through the <c>With...</c> methods on the context this
/// returns.
/// </summary>
public static class AutobahnRunner
{
    /// <summary>Registers the scenarios that make up this test.</summary>
    public static AutobahnContext RegisterScenarios(params ScenarioProps[] scenarios) =>
        AutobahnContext.Empty with { RegisteredScenarios = scenarios };

    /// <summary>Registers the scenarios that make up this test.</summary>
    public static AutobahnContext RegisterScenarios(IEnumerable<ScenarioProps> scenarios) =>
        AutobahnContext.Empty with { RegisteredScenarios = scenarios.ToArray() };

    /// <summary>Runs only the named scenarios out of everything registered.</summary>
    public static AutobahnContext WithTargetScenarios(this AutobahnContext context, params string[] scenarioNames) =>
        ContextResolver.SetTargetScenarios(scenarioNames, context);

    public static AutobahnContext WithTestSuite(this AutobahnContext context, string testSuite) =>
        context with { TestSuite = testSuite };

    public static AutobahnContext WithTestName(this AutobahnContext context, string testName) =>
        context with { TestName = testName };

    /// <summary>Sets the report file name. The default is "autobahn_report_{timestamp}".</summary>
    public static AutobahnContext WithReportFileName(this AutobahnContext context, string reportFileName) =>
        context with { Reporting = context.Reporting with { FileName = reportFileName } };

    /// <summary>Sets the report folder. The default is "./reports/{sessionId}".</summary>
    public static AutobahnContext WithReportFolder(this AutobahnContext context, string reportFolderPath) =>
        context with { Reporting = context.Reporting with { FolderName = reportFolderPath } };

    /// <summary>Sets which report formats to write. The default is all four.</summary>
    public static AutobahnContext WithReportFormats(this AutobahnContext context, params ReportFormat[] reportFormats) =>
        context with { Reporting = context.Reporting with { Formats = reportFormats } };

    /// <summary>Writes no report files. The console summary is still printed.</summary>
    public static AutobahnContext WithoutReports(this AutobahnContext context) =>
        context with { Reporting = context.Reporting with { Formats = [] } };

    /// <summary>How often live statistics are produced. Default and minimum: 5 seconds.</summary>
    public static AutobahnContext WithReportingInterval(this AutobahnContext context, TimeSpan interval) =>
        context with { Reporting = context.Reporting with { ReportingInterval = interval } };

    /// <summary>Registers background worker plugins.</summary>
    public static AutobahnContext WithWorkerPlugins(this AutobahnContext context, params IWorkerPlugin[] plugins) =>
        context with { WorkerPlugins = plugins };

    /// <summary>Loads the JSON config from a file path or an HTTP URL.</summary>
    public static AutobahnContext LoadConfig(this AutobahnContext context, string path)
    {
        var config = ReadConfig(path);

        if (!config.HasAnySetting)
        {
            throw new AutobahnException(
                "The Autobahn config file is empty or doesn't follow the config format. "
                + "Please read the documentation about the Autobahn JSON config format.");
        }

        return context with { Config = config };
    }

    private static AutobahnConfig ReadConfig(string path)
    {
        if (Uri.IsWellFormedUriString(path, UriKind.Absolute))
        {
            using var client = new HttpClient();
            var configJson = client.GetStringAsync(path).GetAwaiter().GetResult();
            return AutobahnJson.Deserialize<AutobahnConfig>(configJson);
        }

        if (Path.GetExtension(path) != ".json")
            throw new AutobahnException($"Unsupported config format: '{path}'. Autobahn reads JSON config files.");

        return AutobahnJson.Deserialize<AutobahnConfig>(File.ReadAllText(path));
    }

    /// <summary>Loads the infrastructure config from a file path or an HTTP URL.</summary>
    public static AutobahnContext LoadInfraConfig(this AutobahnContext context, string path)
    {
        IConfiguration config;

        if (Uri.IsWellFormedUriString(path, UriKind.Absolute))
        {
            using var client = new HttpClient();
            using var configStream = client.GetStreamAsync(path).GetAwaiter().GetResult();
            config = new ConfigurationBuilder().AddJsonStream(configStream).Build();
        }
        else
        {
            if (Path.GetExtension(path) != ".json")
                throw new AutobahnException($"Unsupported infra config format: '{path}'. Autobahn reads JSON config files.");

            config = new ConfigurationBuilder().AddJsonFile(path).Build();
        }

        return context with { InfraConfig = config };
    }

    /// <summary>Sets the minimum log level. The default is Debug.</summary>
    public static AutobahnContext WithMinimumLogLevel(this AutobahnContext context, LogLevel level) =>
        context with { MinimumLogLevel = level };

    /// <summary>
    /// Takes over logging: the supplied builder replaces Autobahn's default rolling file log.
    /// Levels can also be set from the infra config's "Logging" section.
    /// </summary>
    public static AutobahnContext WithLogging(this AutobahnContext context, Action<ILoggingBuilder> configureLogging) =>
        context with { ConfigureLogging = configureLogging };

    /// <summary>
    /// Turns on the hints analyzer, which inspects the final statistics and points out ways
    /// the test was under-instrumented. The default is off.
    /// </summary>
    public static AutobahnContext EnableHintsAnalyzer(this AutobahnContext context, bool enable) =>
        context with { EnableHintsAnalyzer = enable };

    /// <summary>
    /// Ends the run the moment the plan says so, even if the generator is lagging and still
    /// has iterations in flight. The default is off.
    /// </summary>
    public static AutobahnContext EnableStopTestForcibly(this AutobahnContext context, bool enable) =>
        context with { EnableStopTestForcibly = enable };

    /// <summary>
    /// Ends the run when the token is cancelled. The session still stops cleanly and still
    /// writes its reports: cancelling asks for an early finish, not for the results to be
    /// thrown away.
    /// </summary>
    public static AutobahnContext WithCancellationToken(this AutobahnContext context, CancellationToken cancellationToken) =>
        context with { CancellationToken = cancellationToken };

    /// <summary>
    /// Stops collecting the load generator's own CPU, memory, GC, thread-pool and socket
    /// counters. They are collected by default, and are what let a run show it was not itself
    /// the bottleneck; turn them off only when something else is already watching the process.
    /// </summary>
    public static AutobahnContext WithoutRuntimeMetrics(this AutobahnContext context) =>
        context with { EnableRuntimeMetrics = false };

    /// <summary>
    /// Leaves Ctrl+C to the runtime instead of turning it into an early, reported stop.
    /// The default is to handle it.
    /// </summary>
    public static AutobahnContext WithoutCancelKeyPress(this AutobahnContext context) =>
        context with { EnableCancelKeyPress = false };

    /// <summary>
    /// Applies command-line arguments over the context: <c>-c/--config</c>, <c>-i/--infra</c>
    /// and <c>-t/--target</c>.
    /// </summary>
    internal static AutobahnContext ExecuteCliArgs(this AutobahnContext context, IReadOnlyList<string> args)
    {
        var cliArgs = CommandLineArgs.Parse(args);

        if (!string.IsNullOrWhiteSpace(cliArgs.Config)) context = context.LoadConfig(cliArgs.Config);
        if (!string.IsNullOrWhiteSpace(cliArgs.InfraConfig)) context = context.LoadInfraConfig(cliArgs.InfraConfig);

        if (cliArgs.TargetScenarios.Count > 0)
            context = ContextResolver.SetTargetScenarios(cliArgs.TargetScenarios, context);

        return context;
    }

    /// <summary>Runs the session and returns its final statistics.</summary>
    /// <exception cref="AutobahnException">The configuration is invalid or the run failed.</exception>
    public static SessionStats Run(this AutobahnContext context, params string[] args) =>
        context.RunWithResult(args).FinalStats;

    /// <summary>
    /// Runs the session and returns the full result: final statistics, the interval timeline
    /// behind them, and any hints.
    /// </summary>
    /// <exception cref="AutobahnException">The configuration is invalid or the run failed.</exception>
    public static SessionResult RunWithResult(this AutobahnContext context, params string[] args)
    {
        var result = RunInternal(context, args);
        if (result.IsError) throw new AutobahnException(result.Error);

        return result.Value;
    }

    internal static Result<SessionResult> RunInternal(AutobahnContext context, IReadOnlyList<string> args)
    {
        // A load generator that pauses for gen2 in the middle of a run reports its own GC as
        // the target's latency.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        // "disposeLogger=false" keeps the logger alive after the run so a test can read what
        // was written to its own provider.
        var disposeLogger = !args.Contains("disposeLogger=false");

        return args.Count == 0
            ? SessionRunner.Run(disposeLogger, context)
            : SessionRunner.Run(disposeLogger, context.ExecuteCliArgs(args));
    }
}
