namespace Autobahn.Internal.Services;

/// <summary>The arguments a test binary accepts when it is handed a command line.</summary>
internal sealed record CommandLineArgs
{
    public string? Config { get; init; }
    public string? InfraConfig { get; init; }
    public IReadOnlyList<string> TargetScenarios { get; init; } = [];

    /// <summary>Print every effective setting and where it came from, then run as usual.</summary>
    public bool ShowConfig { get; init; }

    /// <summary>
    /// Parses <c>-c/--config</c>, <c>-i/--infra</c>, <c>-t/--target</c> and <c>--show-config</c>.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than delegating to a parser package: three options do not justify
    /// a dependency, and this way an unrecognised argument is ignored rather than aborting the
    /// process, which is what the fork point's parser did and what test suites rely on when
    /// their own runner passes arguments through.
    /// </remarks>
    public static CommandLineArgs Parse(IReadOnlyList<string> args)
    {
        string? config = null;
        string? infraConfig = null;
        var targets = new List<string>();
        var showConfig = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            var (name, inlineValue) = SplitInline(arg);

            switch (name)
            {
                case "-c" or "--config":
                    config = inlineValue ?? Next(args, ref i);
                    break;

                case "-i" or "--infra":
                    infraConfig = inlineValue ?? Next(args, ref i);
                    break;

                case "-t" or "--target":
                    var target = inlineValue ?? Next(args, ref i);
                    if (!string.IsNullOrWhiteSpace(target)) targets.Add(target);
                    break;

                case "--show-config":
                    showConfig = true;
                    break;
            }
        }

        return new CommandLineArgs
        {
            Config = config,
            InfraConfig = infraConfig,
            TargetScenarios = targets,
            ShowConfig = showConfig
        };
    }

    /// <summary>Supports both <c>--config value</c> and <c>--config=value</c>.</summary>
    private static (string Name, string? Value) SplitInline(string arg)
    {
        var separator = arg.IndexOf('=');
        return separator < 0 ? (arg, null) : (arg[..separator], arg[(separator + 1)..]);
    }

    private static string? Next(IReadOnlyList<string> args, ref int i) =>
        i + 1 < args.Count ? args[++i] : null;
}
