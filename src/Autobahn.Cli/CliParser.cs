using System.Globalization;
using Autobahn.Stats;
using Microsoft.Extensions.Logging;

namespace Autobahn.Cli;

/// <summary>
/// Turns an argument array into <see cref="CliOptions"/>.
/// </summary>
/// <remarks>
/// Hand-written, like the engine's own argument parser and for the same reason: the option
/// set is small, and a parser package would decide for us what an unrecognised argument means.
/// Here it is an error with a message that names it, which is what a person mistyping a flag
/// needs - unlike the in-process parser, where an unknown argument belongs to the test runner
/// and has to be ignored.
/// </remarks>
internal static class CliParser
{
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new CliOptions { Command = "help" };

        var first = args[0];

        if (first is "-h" or "--help" or "help") return new CliOptions { Command = "help" };
        if (first is "-v" or "--version" or "version") return new CliOptions { Command = "version" };

        if (first.StartsWith('-'))
            return CliOptions.Failed($"'{first}' is an option, not a command. Try 'autobahn run <file>'.");

        if (first is not ("run" or "list" or "record"))
            return CliOptions.Failed($"'{first}' is not a command. Known commands: run, list, record.");

        var options = new CliOptions { Command = first };
        var formats = new List<ReportFormat>();
        var targets = new List<string>();

        for (var i = 1; i < args.Count; i++)
        {
            var (name, inline) = SplitInline(args[i]);

            if (!name.StartsWith('-'))
            {
                if (options.Source is not null)
                    return CliOptions.Failed($"'{name}' is a second source; '{options.Source}' was already given.");

                options = options with { Source = name };
                continue;
            }

            switch (name)
            {
                case "-t" or "--target":
                    if (Value(args, ref i, inline) is not { } target)
                        return Missing(name);
                    targets.Add(target);
                    break;

                case "-c" or "--config":
                    if (Value(args, ref i, inline) is not { } config) return Missing(name);
                    options = options with { ConfigPath = config };
                    break;

                case "-i" or "--infra":
                    if (Value(args, ref i, inline) is not { } infra) return Missing(name);
                    options = options with { InfraConfigPath = infra };
                    break;

                case "-o" or "--out":
                    if (Value(args, ref i, inline) is not { } folder) return Missing(name);
                    options = options with { ReportFolder = folder };
                    break;

                case "-n" or "--name":
                    if (Value(args, ref i, inline) is not { } reportName) return Missing(name);
                    options = options with { ReportFileName = reportName };
                    break;

                case "-f" or "--format":
                    if (Value(args, ref i, inline) is not { } formatList) return Missing(name);

                    foreach (var raw in formatList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!Enum.TryParse<ReportFormat>(raw, ignoreCase: true, out var format))
                        {
                            return CliOptions.Failed(
                                $"'{raw}' is not a report format. Known formats: "
                                + $"{string.Join(", ", Enum.GetNames<ReportFormat>())}.");
                        }

                        formats.Add(format);
                    }

                    break;

                case "--reporting-interval":
                    if (Value(args, ref i, inline) is not { } intervalText) return Missing(name);

                    if (!TimeSpan.TryParse(intervalText, CultureInfo.InvariantCulture, out var interval))
                        return CliOptions.Failed($"'{intervalText}' is not a duration. Try 00:00:10.");

                    options = options with { ReportingInterval = interval };
                    break;

                case "-l" or "--log-level":
                    if (Value(args, ref i, inline) is not { } levelText) return Missing(name);

                    if (!Enum.TryParse<LogLevel>(levelText, ignoreCase: true, out var level))
                    {
                        return CliOptions.Failed(
                            $"'{levelText}' is not a log level. Known levels: {string.Join(", ", Enum.GetNames<LogLevel>())}.");
                    }

                    options = options with { MinimumLogLevel = level };
                    break;

                case "--suite":
                    if (Value(args, ref i, inline) is not { } suite) return Missing(name);
                    options = options with { TestSuite = suite };
                    break;

                case "--test-name":
                    if (Value(args, ref i, inline) is not { } testName) return Missing(name);
                    options = options with { TestName = testName };
                    break;

                case "--show-config":
                    options = options with { ShowConfig = true };
                    break;

                case "--no-runtime-metrics":
                    options = options with { NoRuntimeMetrics = true };
                    break;

                case "--no-reports":
                    options = options with { NoReports = true };
                    break;

                case "--headless":
                    options = options with { Headless = true };
                    break;

                case "--include-assets":
                    options = options with { IncludeAssets = true };
                    break;

                case "--all-origins":
                    options = options with { SameOriginOnly = false };
                    break;

                case "--namespace":
                    if (Value(args, ref i, inline) is not { } ns) return Missing(name);
                    options = options with { RecordNamespace = ns };
                    break;

                case "--keep-browser-headers":
                    options = options with { KeepBrowserHeaders = true };
                    break;

                case "--ui":
                    options = options with { Ui = true };
                    break;

                case "--no-ui":
                    options = options with { Ui = false };
                    break;

                case "--ui-port":
                    if (Value(args, ref i, inline) is not { } portText) return Missing(name);

                    if (!int.TryParse(portText, out var port) || port is < 0 or > 65535)
                        return CliOptions.Failed($"'{portText}' is not a port.");

                    options = options with { Ui = options.Ui ?? true, UiPort = port };
                    break;

                case "--ui-public":
                    options = options with { Ui = options.Ui ?? true, UiPublic = true };
                    break;

                case "--ui-open":
                    options = options with { Ui = options.Ui ?? true, UiOpen = true };
                    break;

                case "--browser-path":
                    if (Value(args, ref i, inline) is not { } browser) return Missing(name);
                    options = options with { BrowserPath = browser };
                    break;

                default:
                    return CliOptions.Failed($"'{name}' is not an option this version understands.");
            }
        }

        if (options.Source is null)
        {
            return CliOptions.Failed(first == "record"
                ? "'autobahn record' needs a URL to open."
                : $"'autobahn {first}' needs a file: an assembly, or a C# script.");
        }

        return options with { ReportFormats = formats, TargetScenarios = targets };
    }

    private static CliOptions Missing(string name) => CliOptions.Failed($"'{name}' needs a value.");

    private static (string Name, string? Value) SplitInline(string arg)
    {
        var separator = arg.IndexOf('=');
        return separator < 0 ? (arg, null) : (arg[..separator], arg[(separator + 1)..]);
    }

    private static string? Value(IReadOnlyList<string> args, ref int i, string? inline)
    {
        if (inline is not null) return inline;
        return i + 1 < args.Count ? args[++i] : null;
    }
}
