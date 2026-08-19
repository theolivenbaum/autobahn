using System.Diagnostics;
using System.Net;
using Autobahn.Cli.Ui;
using Autobahn.Stats;

namespace Autobahn.Cli;

/// <summary>What the CLI's verbs actually do.</summary>
internal static class Commands
{
    /// <summary>Loads the scenarios a source exposes, whichever kind of source it is.</summary>
    public static Task<IReadOnlyList<ScenarioProps>> LoadScenarios(string source) =>
        ScriptScenarioLoader.IsScript(source)
            ? ScriptScenarioLoader.LoadAsync(source)
            : Task.FromResult(AssemblyScenarioLoader.Load(source));

    public static async Task<int> List(CliOptions options)
    {
        var scenarios = await LoadScenarios(options.Source!).ConfigureAwait(false);

        Console.WriteLine($"{scenarios.Count} scenario(s) in {Path.GetFileName(options.Source)}:");
        Console.WriteLine();

        foreach (var scenario in scenarios.OrderBy(x => x.ScenarioName, StringComparer.Ordinal))
        {
            var simulations = scenario.LoadSimulations.Count == 0
                ? "no load simulations"
                : string.Join(", ", scenario.LoadSimulations.Select(Describe));

            Console.WriteLine($"  {scenario.ScenarioName}");
            Console.WriteLine($"    {simulations}");

            if (scenario.WarmUpDuration is { } warmUp) Console.WriteLine($"    warm-up: {warmUp}");
            if (scenario.Weight is { } weight) Console.WriteLine($"    weight: {weight}");
        }

        return AutobahnExitCode.Ok;
    }

    public static async Task<int> Run(CliOptions options)
    {
        var scenarios = await LoadScenarios(options.Source!).ConfigureAwait(false);
        var context = Apply(options, AutobahnRunner.RegisterScenarios([.. scenarios]));

        if (!WantsUi(options)) return Verdict(context.Run());

        await using var ui = await UiSession.StartAsync(UiSettings(options), CancellationToken.None)
            .ConfigureAwait(false);

        Announce(ui.Url, options);

        var stats = ui.Attach(context, scenarios).Run();

        // Published before the process can exit, so a page watching ends on the run's last
        // word rather than on whatever interval happened to be its last.
        ui.Complete(stats);

        await WaitBeforeClosing(ui.Url).ConfigureAwait(false);

        return Verdict(stats);
    }

    /// <summary>
    /// Renders a finished run as one self-contained page.
    /// </summary>
    /// <remarks>
    /// The same application the live view is, reading a snapshot the exporter wrote into the
    /// document rather than one arriving over a socket - so a finished run and a running one
    /// are looked at through the same screens and cannot disagree about what happened.
    /// </remarks>
    public static int Export(CliOptions options)
    {
        if (options.Source is null)
        {
            Console.Error.WriteLine("autobahn export needs a run artifact: the .json report a run writes.");
            return AutobahnExitCode.Error;
        }

        var written = StaticExport.Write(options.Source, options.OutputPath);
        if (written is null) return AutobahnExitCode.Error;

        var size = new FileInfo(written).Length;

        Console.WriteLine($"Wrote {written} ({size / 1024 / 1024} MB).");
        Console.WriteLine("It is one file: open it anywhere, no server and no network.");

        return AutobahnExitCode.Ok;
    }

    // The run has already set the exit code if a threshold failed; saying so again in words
    // would double up on the message the reports carry.
    private static int Verdict(SessionStats stats) =>
        stats.AllThresholdsPassed ? AutobahnExitCode.Ok : AutobahnExitCode.ThresholdFailed;

    /// <summary>
    /// Holds the server open after the run so the final state can be read.
    /// </summary>
    /// <remarks>
    /// A run that ends the instant its plan does takes the page with it, and the last minute
    /// of a load test is often the interesting one. So an interactive terminal waits for
    /// Ctrl+C; anything else exits, because a CI job hanging until it is killed is worse than
    /// a page nobody was looking at.
    /// </remarks>
    private static async Task WaitBeforeClosing(string url)
    {
        if (!HasTerminal()) return;

        using var closed = new CancellationTokenSource();

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            closed.Cancel();
        }

        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            Console.WriteLine();
            Console.WriteLine($"The run has finished. The live view is still up at {url}");
            Console.WriteLine("Press Ctrl+C to close it.");

            await Task.Delay(Timeout.InfiniteTimeSpan, closed.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C, which is how this is meant to end.
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }
    }

    /// <summary>
    /// Whether to serve the live UI. Unset follows the terminal.
    /// </summary>
    /// <remarks>
    /// On for an interactive terminal, off without one. CI is the case where nobody is going
    /// to open it and an extra listening port is a liability rather than a feature.
    /// </remarks>
    private static bool WantsUi(CliOptions options) => options.Ui ?? HasTerminal();

    private static bool HasTerminal()
    {
        try
        {
            return !Console.IsOutputRedirected && Console.WindowHeight > 0;
        }
        catch
        {
            return false;
        }
    }

    private static UiOptions UiSettings(CliOptions options) => new()
    {
        Port = options.UiPort,
        BindAddress = options.UiPublic ? IPAddress.Any : IPAddress.Loopback,
        OpenBrowser = options.UiOpen
    };

    private static void Announce(string url, CliOptions options)
    {
        if (options.UiPublic)
        {
            Console.WriteLine();
            Console.WriteLine("WARNING: the live UI is bound to every interface, and it can stop this run.");
            Console.WriteLine("         Anyone who can reach this machine and has the URL can use it.");
        }

        Console.WriteLine();
        Console.WriteLine($"Live view: {url}");
        Console.WriteLine();

        if (options.UiOpen) OpenBrowser(url);
    }

    /// <summary>
    /// Opens the URL in whatever the platform considers a browser.
    /// </summary>
    /// <remarks>
    /// Best-effort by design. A headless box, a locked-down desktop or a missing handler are
    /// all normal, and none of them is a reason to fail a load test - the URL is on screen
    /// either way.
    /// </remarks>
    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(Could not open a browser: {ex.Message})");
        }
    }

    /// <summary>
    /// Lays the command line over the context.
    /// </summary>
    /// <remarks>
    /// Everything here goes on as if it had been written in code, which puts it at the "Code"
    /// layer rather than a layer of its own - so a JSON config still wins over a flag. That is
    /// the same order a test binary passing its own arguments through already had, and
    /// changing it for the CLI alone would give the same file two meanings.
    /// </remarks>
    private static AutobahnContext Apply(CliOptions options, AutobahnContext context)
    {
        if (options.ConfigPath is { } config) context = context.LoadConfig(config);
        if (options.InfraConfigPath is { } infra) context = context.LoadInfraConfig(infra);

        if (options.TargetScenarios.Count > 0) context = context.WithTargetScenarios([.. options.TargetScenarios]);

        if (options.TestSuite is { } suite) context = context.WithTestSuite(suite);
        if (options.TestName is { } testName) context = context.WithTestName(testName);

        if (options.ReportFolder is { } folder) context = context.WithReportFolder(folder);
        if (options.ReportFileName is { } name) context = context.WithReportFileName(name);
        if (options.ReportFormats.Count > 0) context = context.WithReportFormats([.. options.ReportFormats]);
        if (options.NoReports) context = context.WithoutReports();

        if (options.ReportingInterval is { } interval) context = context.WithReportingInterval(interval);
        if (options.MinimumLogLevel is { } level) context = context.WithMinimumLogLevel(level);

        if (options.NoRuntimeMetrics) context = context.WithoutRuntimeMetrics();
        if (options.ShowConfig) context = context.ShowEffectiveConfig();

        return context;
    }

    private static string Describe(LoadSimulation simulation) => simulation switch
    {
        LoadSimulation.RampingConstant x => $"ramping_constant {x.Copies} over {x.During}",
        LoadSimulation.KeepConstant x => $"keep_constant {x.Copies} for {x.During}",
        LoadSimulation.RampingInject x => $"ramping_inject {x.Rate}/{x.Interval} over {x.During}",
        LoadSimulation.Inject x => $"inject {x.Rate}/{x.Interval} for {x.During}",
        LoadSimulation.InjectRandom x => $"inject_random {x.MinRate}-{x.MaxRate}/{x.Interval} for {x.During}",
        LoadSimulation.IterationsForConstant x => $"iterations_for_constant {x.Iterations} over {x.Copies} copies",
        LoadSimulation.IterationsForInject x => $"iterations_for_inject {x.Iterations} at {x.Rate}/{x.Interval}",
        LoadSimulation.Pause x => $"pause {x.During}",
        _ => simulation.GetType().Name
    };
}
