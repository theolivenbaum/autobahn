using Autobahn.Configuration;
using Autobahn.Plugins;
using Autobahn.Thresholds;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// Providers added beside whatever logging is already configured, rather than instead of
    /// it.
    /// </summary>
    /// <remarks>
    /// <see cref="ConfigureLogging"/> replaces the default file log, which is right when the
    /// caller wants their own logging and wrong when something merely wants to *watch* - the
    /// live UI tails the run's log without meaning to stop it being written to disk.
    /// </remarks>
    public IReadOnlyList<ILoggerProvider> AdditionalLoggerProviders { get; init; } = [];

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

    /// <summary>
    /// Whether the load generator's own CPU, memory, GC, thread-pool and socket counters are
    /// collected alongside the scenario stats. On by default: a load test that cannot show it
    /// was not itself the bottleneck is not evidence.
    /// </summary>
    public required bool EnableRuntimeMetrics { get; init; }

    /// <summary>The pass/fail rules this run is gated on. Empty means the run cannot fail.</summary>
    public required IReadOnlyList<Threshold> Thresholds { get; init; }

    /// <summary>
    /// Whether a failed threshold sets a non-zero process exit code. On by default, because a
    /// CI gate that always exits zero is decorative.
    /// </summary>
    public required bool EnableThresholdExitCode { get; init; }

    /// <summary>
    /// Whether the run prints every effective setting and the layer its value came from
    /// before it starts. Off by default; also turned on by <c>--show-config</c>.
    /// </summary>
    public required bool ShowEffectiveConfig { get; init; }

    /// <summary>
    /// Called with each reporting interval's numbers as the run produces them, or null.
    /// </summary>
    /// <remarks>
    /// One delegate, deliberately - not the <c>IReportingSink</c> contract the fork point had.
    /// That was an interface with its own lifecycle that user code implemented and Autobahn
    /// drove; this is a callback handed the record the engine already built. Exporting a run
    /// somewhere is the caller's business, and the run artifact covers the end-of-run case.
    /// </remarks>
    public Func<Stats.TimeLineHistoryRecord, Task>? OnInterval { get; init; }

    /// <summary>
    /// Called once, with the run as it was finally resolved, before any load is generated.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="OnInterval"/>: that one carries what happened, this one
    /// carries what is about to be attempted. Anything watching a run needs both, and only the
    /// engine knows what the JSON config, the environment and the command line settled on
    /// between them.
    /// </remarks>
    public Func<Stats.SessionStartInfo, Task>? OnSessionStart { get; init; }

    /// <summary>
    /// The clock the engine schedules on. <see cref="TimeProvider.System"/> unless a test
    /// replaced it.
    /// </summary>
    /// <remarks>
    /// This is the engine's *scheduling* clock - the reporting tick, the warm-up cut-off, the
    /// simulation interval, the shutdown poll, the runtime-metrics sampler - and deliberately
    /// not its measuring clock. Latency is still read from <see cref="System.Diagnostics.Stopwatch"/>,
    /// which is a static intrinsic; <c>TimeProvider.GetTimestamp</c> is a virtual call, and
    /// paying one per measurement to make a number that is never faked fakeable is the kind
    /// of self-inflicted cost the benchmarks exist to catch.
    ///
    /// What it does buy is a test that can drive a whole session without waiting for it: hand
    /// the runner a <c>FakeTimeProvider</c> and the intervals, the warm-up and the shutdown
    /// poll all advance when the test says so.
    /// </remarks>
    public required TimeProvider TimeProvider { get; init; }

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
        EnableCancelKeyPress = true,
        EnableRuntimeMetrics = true,
        Thresholds = [],
        EnableThresholdExitCode = true,
        ShowEffectiveConfig = false,
        OnInterval = null,
        OnSessionStart = null,
        TimeProvider = TimeProvider.System
    };
}
