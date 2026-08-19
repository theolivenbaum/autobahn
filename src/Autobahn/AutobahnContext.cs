using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Autobahn.Configuration;
using Autobahn.Plugins;

namespace Autobahn;

/// <summary>
/// Everything a session needs to run: the scenarios, the configuration layered over them,
/// and where the results go. Build one with <see cref="AutobahnRunner"/>.
/// </summary>
public sealed record AutobahnContext
{
    public required string TestSuite { get; init; }
    public required string TestName { get; init; }
    public required IReadOnlyList<ScenarioProps> RegisteredScenarios { get; init; }

    /// <summary>The JSON config, when one was loaded.</summary>
    public AutobahnConfig? Config { get; init; }

    /// <summary>The infrastructure config, handed to plugins and used to configure logging.</summary>
    public IConfiguration? InfraConfig { get; init; }

    /// <summary>Replaces Autobahn's default file logging when set.</summary>
    public Action<ILoggingBuilder>? ConfigureLogging { get; init; }

    public required ReportingContext Reporting { get; init; }
    public required IReadOnlyList<IWorkerPlugin> WorkerPlugins { get; init; }
    public required bool EnableHintsAnalyzer { get; init; }

    /// <summary>Null runs every registered scenario.</summary>
    public IReadOnlyList<string>? TargetScenarios { get; init; }

    public LogLevel? MinimumLogLevel { get; init; }
    public required bool EnableStopTestForcibly { get; init; }

    /// <summary>
    /// Ends the run when this is cancelled. The session still stops cleanly and still writes
    /// its reports - cancelling asks for an early finish, not for the results to be thrown away.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Whether Ctrl+C ends the run early instead of killing the process. On by default when
    /// there is a console to press it at.
    /// </summary>
    public required bool EnableCancelKeyPress { get; init; }

    public static AutobahnContext Empty { get; } = new()
    {
        TestSuite = Constants.DefaultTestSuite,
        TestName = Constants.DefaultTestName,
        RegisteredScenarios = [],
        Config = null,
        InfraConfig = null,
        ConfigureLogging = null,
        Reporting = new ReportingContext
        {
            FolderName = null,
            FileName = null,
            Formats = Constants.AllReportFormats,
            ReportingInterval = Constants.DefaultReportingInterval
        },
        WorkerPlugins = [],
        EnableHintsAnalyzer = false,
        TargetScenarios = null,
        MinimumLogLevel = null,
        EnableStopTestForcibly = false,
        CancellationToken = default,
        EnableCancelKeyPress = true
    };
}
