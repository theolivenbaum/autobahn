namespace Autobahn;

using Autobahn.Stats;

/// <summary>Every tunable default in one place.</summary>
internal static class Constants
{
    public const string Logo = "Autobahn";
    public const string WelcomeText = "Autobahn {0} started a new session: {1}";

    public const int DefaultCopiesCount = 1;

    public static readonly ReportFormat[] AllReportFormats =
        [ReportFormat.Txt, ReportFormat.Html, ReportFormat.Csv, ReportFormat.Md, ReportFormat.Json];

    /// <summary>
    /// The shape of the run artifact. Bumped when a field is removed or its meaning changes;
    /// adding one does not bump it, because a reader that ignores unknown fields still works.
    /// </summary>
    public const int RunArtifactSchemaVersion = 1;

    public const string DefaultTestSuite = "autobahn_default_test_suite_name";
    public const string DefaultTestName = "autobahn_default_test_name";
    public const string DefaultReportName = "autobahn_report";
    public const string DefaultReportFolder = "reports";
    public const string LogFilePrefix = "autobahn-log";

    /// <summary>The reserved step name under which a scenario's own iteration is measured.</summary>
    public const string ScenarioGlobalInfo = "global information";

    // Default timeouts.

    public static readonly TimeSpan DefaultSimulationDuration = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MinSimulationDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultWarmUpDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MinReportingInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultReportingInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan GetPluginStatsTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);
    /// <summary>
    /// How long the reporting manager waits after stopping its timer, so measurements already
    /// in the stats actor's mailbox land before the final statistics are built. Deliberately
    /// short: it is a drain, not a reporting interval, and it must never be long enough for
    /// another tick to fire.
    /// </summary>
    public static readonly TimeSpan ReportingManagerDrainDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>10 minutes, in ticks.</summary>
    public const long MaxTrackableStepLatency = 1000L * TimeSpan.TicksPerMillisecond * 60L * 10L;

    public const long MaxTrackableStepResponseSize = long.MaxValue;

    public const int ConsoleRefreshTableCounter = 13;

    /// <summary>The width the console report is laid out at when there is no terminal to measure.</summary>
    public const int NonInteractiveConsoleWidth = 140;
    /// <summary>How long in-flight iterations get to finish after a scenario's plan ends.</summary>
    public static readonly TimeSpan DefaultCompletionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How often the shutdown path re-checks whether the actors have drained.</summary>
    public static readonly TimeSpan ShutdownPollInterval = TimeSpan.FromMilliseconds(25);

    // Metrics.

    /// <summary>How often the runtime metrics are sampled, independent of the reporting interval.</summary>
    public static readonly TimeSpan MetricsSampleInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>The ceiling of a metric histogram, in hundredths of the metric's raw unit.</summary>
    public const long MaxTrackableMetricValue = 1_000_000_000_000L;

    public const int MetricSignificantDigits = 3;

    /// <summary>
    /// How many runtime samples pass between reads of the process thread count. Enumerating
    /// every thread in the process is the most expensive counter there is, and the number it
    /// produces moves slowly.
    /// </summary>
    public const int ProcessThreadSampleEvery = 8;

    /// <summary>How often the runtime is asked to publish its socket event counters, in seconds.</summary>
    public const int SocketCounterIntervalSec = 1;

    public const string MetricCpuPercent = "runtime.cpu";
    public const string MetricWorkingSet = "runtime.working_set";
    public const string MetricGcHeap = "runtime.gc_heap";
    public const string MetricGen0Collections = "runtime.gc_gen0";
    public const string MetricGen1Collections = "runtime.gc_gen1";
    public const string MetricGen2Collections = "runtime.gc_gen2";
    public const string MetricThreadPoolQueue = "runtime.threadpool_queue";
    public const string MetricThreadPoolThreads = "runtime.threadpool_threads";
    public const string MetricThreads = "runtime.threads";
    public const string MetricSocketBytesSent = "runtime.socket_sent";
    public const string MetricSocketBytesReceived = "runtime.socket_received";

    // Thresholds.

    /// <summary>The process exit code a run sets when one of its thresholds failed.</summary>
    public const int ThresholdFailedExitCode = AutobahnExitCode.ThresholdFailed;

    public const string StopReasonThreshold = "a threshold was violated";

    // Why a run ended before its plan did.

    public const string StopReasonCancelled = "the caller cancelled the session";
    public const string StopReasonCtrlC = "Ctrl+C: stopping the test and writing what it measured so far";

    // Default status codes.

    public const string OperationTimeoutMessage = "operation timeout";
    public const string TimeoutStatusCode = "-100";
    public const string UnhandledExceptionCode = "-101";

    /// <summary>An iteration Autobahn cancelled because it outran its own timeout.</summary>
    public const string IterationTimeoutStatusCode = "-102";
    public const string IterationTimeoutMessage = "iteration timeout";

    public const int StatsRounding = 2;
    public const int ScenarioMaxFailCount = 5_000;
}
