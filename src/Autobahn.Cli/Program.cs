using System.Reflection;

namespace Autobahn.Cli;

/// <summary>
/// The Autobahn command-line front end.
/// </summary>
/// <remarks>
/// A skeleton on purpose: the terminal UI (Terminal.Gui) and the Kestrel-hosted web UI are
/// TODO.md sections 6 and 8, and the engine has to stay fully usable headless without either.
/// What exists here is the entry point and the argument surface they will hang off, so the
/// tool packages and runs from day one instead of appearing all at once at the end.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.WriteLine(GetVersion());
            return 0;
        }

        Console.Error.WriteLine(
            $"autobahn: '{args[0]}' is not a command this version understands. Run 'autobahn --help'.");

        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            autobahn {GetVersion()} - load testing for .NET

            Usage:
              autobahn [command] [options]

            Options:
              -h, --help       Show this help.
              -v, --version    Show the version.

            Nothing else is wired up yet. Today a load test is a .NET program that references
            the Autobahn package and calls AutobahnRunner; this tool is where running one from
            the command line, watching it in the terminal and serving the web UI will live.
            See TODO.md, sections 6 and 8.
            """);
    }

    private static string GetVersion() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
