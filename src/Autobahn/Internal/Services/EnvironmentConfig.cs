using Autobahn.Stats;

namespace Autobahn.Internal.Services;

/// <summary>
/// The settings Autobahn reads from the environment, under the <c>AUTOBAHN_</c> prefix.
/// </summary>
/// <remarks>
/// A layer between the JSON config and the command line, which is where a CI system usually
/// wants to sit: it can set a report folder or narrow the target scenarios for one job without
/// editing the config the repository ships or rewriting the invocation.
///
/// Only settings whose value is a scalar are here. A load plan or a threshold belongs in the
/// config file, where it can be read; squeezing one into an environment variable would be a
/// syntax nobody could remember.
/// </remarks>
internal static class EnvironmentConfig
{
    public const string Prefix = "AUTOBAHN_";

    public static string? TestSuite => Read("TEST_SUITE");
    public static string? TestName => Read("TEST_NAME");
    public static string? ReportFileName => Read("REPORT_NAME");
    public static string? ReportFolder => Read("REPORT_FOLDER");

    public static IReadOnlyList<string>? TargetScenarios => ReadList("TARGET_SCENARIOS");

    public static TimeSpan? ReportingInterval => ReadTimeSpan("REPORTING_INTERVAL");

    public static bool? EnableHintsAnalyzer => ReadBool("ENABLE_HINTS");
    public static bool? EnableRuntimeMetrics => ReadBool("ENABLE_RUNTIME_METRICS");

    public static IReadOnlyList<ReportFormat>? ReportFormats
    {
        get
        {
            var raw = ReadList("REPORT_FORMATS");
            if (raw is null) return null;

            var formats = new List<ReportFormat>(raw.Count);

            foreach (var name in raw)
            {
                // An unrecognised format is skipped rather than failing the run: an
                // environment variable is often set by something that does not know what
                // this version supports.
                if (Enum.TryParse<ReportFormat>(name, ignoreCase: true, out var format)) formats.Add(format);
            }

            return formats.Count > 0 ? formats : null;
        }
    }

    private static string? Read(string name)
    {
        var value = Environment.GetEnvironmentVariable(Prefix + name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static IReadOnlyList<string>? ReadList(string name) =>
        Read(name)
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } parts
            ? parts
            : null;

    private static TimeSpan? ReadTimeSpan(string name) =>
        Read(name) is { } value && TimeSpan.TryParse(value, out var parsed) ? parsed : null;

    private static bool? ReadBool(string name) =>
        Read(name) is { } value && bool.TryParse(value, out var parsed) ? parsed : null;
}
