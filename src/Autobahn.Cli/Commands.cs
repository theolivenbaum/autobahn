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

        var stats = context.Run();

        // The run has already set the exit code if a threshold failed; saying so again here
        // would double up on the message the reports carry.
        return stats.AllThresholdsPassed ? AutobahnExitCode.Ok : AutobahnExitCode.ThresholdFailed;
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
