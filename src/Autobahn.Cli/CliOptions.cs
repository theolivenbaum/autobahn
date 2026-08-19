using Microsoft.Extensions.Logging;
using Autobahn.Stats;

namespace Autobahn.Cli;

/// <summary>What the command line asked for.</summary>
internal sealed record CliOptions
{
    /// <summary>The verb: <c>run</c>, <c>list</c>, <c>record</c>, <c>help</c> or <c>version</c>.</summary>
    public required string Command { get; init; }

    /// <summary>The assembly or script holding the scenarios.</summary>
    public string? Source { get; init; }

    public IReadOnlyList<string> TargetScenarios { get; init; } = [];
    public string? ConfigPath { get; init; }
    public string? InfraConfigPath { get; init; }
    public string? ReportFolder { get; init; }
    public string? ReportFileName { get; init; }
    public IReadOnlyList<ReportFormat> ReportFormats { get; init; } = [];
    public TimeSpan? ReportingInterval { get; init; }
    public LogLevel? MinimumLogLevel { get; init; }
    public string? TestSuite { get; init; }
    public string? TestName { get; init; }
    public bool ShowConfig { get; init; }
    public bool NoRuntimeMetrics { get; init; }
    public bool NoReports { get; init; }

    // record only.

    /// <summary>Records without a visible browser window. Only the page load is captured then.</summary>
    public bool Headless { get; init; }

    /// <summary>Keeps images, stylesheets, fonts and scripts in the recording.</summary>
    public bool IncludeAssets { get; init; }

    /// <summary>Records only requests to the origin the session started on. On by default.</summary>
    public bool SameOriginOnly { get; init; } = true;

    /// <summary>
    /// Emits a class in this namespace instead of a script. Null writes a <c>.csx</c> the CLI
    /// can run straight away.
    /// </summary>
    public string? RecordNamespace { get; init; }

    /// <summary>
    /// A Chromium to use instead of the one Playwright installed for itself.
    /// </summary>
    /// <remarks>
    /// CI images and dev containers often already carry a browser, and it is rarely the exact
    /// build the Playwright package pins - which otherwise fails with a message about a
    /// missing executable rather than about a version.
    /// </remarks>
    public string? BrowserPath { get; init; }

    /// <summary>Keeps the browser's own user-agent and sec-* headers in the recording.</summary>
    public bool KeepBrowserHeaders { get; init; }

    /// <summary>Set when the command line could not be understood; nothing else is valid then.</summary>
    public string? Error { get; init; }

    public static CliOptions Failed(string error) => new() { Command = "help", Error = error };
}
