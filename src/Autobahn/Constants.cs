namespace Autobahn;

using Autobahn.Stats;

/// <summary>Every tunable default in one place.</summary>
internal static class Constants
{
    public const string Logo = "Autobahn";
    public const string WelcomeText = "Autobahn {0} started a new session: {1}";

    public const int DefaultCopiesCount = 1;

    public static readonly ReportFormat[] AllReportFormats =
        [ReportFormat.Txt, ReportFormat.Html, ReportFormat.Csv, ReportFormat.Md];

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
    public static readonly TimeSpan ReportingManagerStartDelay = TimeSpan.FromSeconds(3);

    /// <summary>10 minutes, in ticks.</summary>
    public const long MaxTrackableStepLatency = 1000L * TimeSpan.TicksPerMillisecond * 60L * 10L;

    public const long MaxTrackableStepResponseSize = long.MaxValue;

    public const int ConsoleRefreshTableCounter = 13;

    /// <summary>The width the console report is laid out at when there is no terminal to measure.</summary>
    public const int NonInteractiveConsoleWidth = 140;
    public const int MaxWaitWorkingActorsSec = 60;

    // Default status codes.

    public const string OperationTimeoutMessage = "operation timeout";
    public const string TimeoutStatusCode = "-100";
    public const string UnhandledExceptionCode = "-101";

    public const int StatsRounding = 2;
    public const int ScenarioMaxFailCount = 5_000;
}
