using System.Reflection;

namespace Autobahn.Cli;

/// <summary>
/// The Autobahn command-line front end.
/// </summary>
/// <remarks>
/// A load test is still an ordinary .NET program that references the package and calls the
/// runner - that shape does not go away. This tool is the other route: point it at a built
/// assembly or a single C# script that exposes scenarios, and it builds the run around them,
/// so every option about reports, targets and logging lives on the command line rather than
/// having to be threaded through the program's own <c>Main</c>.
///
/// The terminal dashboard and the Kestrel-hosted web UI are TODO.md section 8.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = CliParser.Parse(args);

        if (options.Error is { } error)
        {
            Console.Error.WriteLine($"autobahn: {error}");
            Console.Error.WriteLine("Run 'autobahn --help'.");
            return AutobahnExitCode.Error;
        }

        try
        {
            return options.Command switch
            {
                "version" => Version(),
                "list" => await Commands.List(options).ConfigureAwait(false),
                "run" => await Commands.Run(options).ConfigureAwait(false),
                "record" => await RecordCommand.Run(options).ConfigureAwait(false),
                _ => Help()
            };
        }
        catch (AutobahnException ex)
        {
            // Autobahn's own errors already read as sentences; a stack trace on top of one
            // helps nobody at a prompt.
            Console.Error.WriteLine($"autobahn: {ex.Message}");
            return AutobahnExitCode.Error;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"autobahn: {ex}");
            return AutobahnExitCode.Error;
        }
    }

    private static int Version()
    {
        Console.WriteLine(GetVersion());
        return AutobahnExitCode.Ok;
    }

    private static int Help()
    {
        Console.WriteLine($"""
            autobahn {GetVersion()} - load testing for .NET

            Usage:
              autobahn run    <file> [options]  Run the scenarios a file exposes.
              autobahn list   <file>            List them without running anything.
              autobahn record <url>  [options]  Learn a scenario by watching a browser session.

            <file> is either a built assembly, or a single C# script (.cs / .csx) that
            returns a scenario or a list of them as its last expression. In an assembly a
            scenario source is a public static property, or a public static parameterless
            method, returning ScenarioProps or a sequence of them - mark them
            [ScenarioSource] to say which ones you meant.

            'record' opens a real browser, watches every request the page makes, and writes
            the scenario source for what happened. It is not browser-driven load testing:
            browsers under load make the generator the bottleneck. Learn from one session,
            then hammer with an HTTP client.

            Options:
              -t, --target <name>        Run only this scenario. Repeatable.
              -c, --config <path>        Load a JSON config file or URL.
              -i, --infra <path>         Load an infrastructure config file or URL.
              -o, --out <folder>         Where the reports go.
              -n, --name <name>          What the report files are called.
              -f, --format <list>        Report formats, comma-separated: Txt, Html, Csv, Md, Json.
              -l, --log-level <level>    Trace, Debug, Information, Warning, Error, Critical, None.
                  --suite <name>         Test suite name.
                  --test-name <name>     Test name.
                  --reporting-interval <duration>
                                         How often live statistics are produced, e.g. 00:00:10.
                  --show-config          Print every effective setting and where it came from.
                  --no-runtime-metrics   Do not collect the load generator's own CPU, GC and so on.
                  --no-reports           Write no report files. The console summary still prints.
              -h, --help                 Show this help.
              -v, --version              Show the version.

            Options for 'run', for the live web view:
                  --ui                   Serve it. On by default at a terminal, off without one.
                  --no-ui                Do not serve it.
                  --ui-port <port>       The port. 0, the default, picks a free one.
                  --ui-public            Serve on every interface rather than loopback. Loud on
                                         purpose: this surface can stop the run.
                  --ui-open              Open the printed URL in a browser.

            Options for 'record':
              -n, --name <path>          Where the generated file goes.
                  --namespace <ns>       Emit a class in this namespace instead of a .csx script.
                  --headless             Record without a browser window; captures the page load only.
                  --include-assets       Keep images, stylesheets, fonts and scripts.
                  --all-origins          Record third-party requests too, not just the site's own.
                  --keep-browser-headers Keep the browser's user-agent and sec-* headers.
                  --browser-path <path>  Use a Chromium this machine already has, rather than
                                         the build the Playwright package pins. Also
                                         AUTOBAHN_BROWSER_PATH.
                  --test-name <name>     What the generated scenario is called.

            Environment variables layer between the JSON config and the command line, under
            the AUTOBAHN_ prefix: AUTOBAHN_REPORT_FOLDER, AUTOBAHN_TARGET_SCENARIOS,
            AUTOBAHN_REPORT_FORMATS, AUTOBAHN_REPORTING_INTERVAL and a few more.

            Exit codes:
              0  the run finished and every threshold passed
              1  the command line, the file or the run itself was wrong
              2  the run finished and a threshold failed

            The web view is for watching a run while it happens. A finished run is read from
            the reports it wrote - the html one for a person, the json one for a machine.
            """);

        return AutobahnExitCode.Ok;
    }

    private static string GetVersion() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
