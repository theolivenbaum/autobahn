using Spectre.Console;
using Autobahn.Internal.Domain;
using Autobahn.Internal.Infra;
using Autobahn.Stats;

namespace Autobahn.Internal.Services.TestHost;

/// <summary>Runs the scenarios' own init and clean functions around the session.</summary>
internal static class TestHostScenario
{
    public static List<RuntimeScenario> GetTargetScenarios(
        SessionArgs sessionArgs, IReadOnlyList<RuntimeScenario> regScenarios) =>
        ScenarioFactory.ApplySettings(
            sessionArgs.ScenariosSettings,
            ScenarioFactory.FilterTargetScenarios(sessionArgs.TargetScenarios, regScenarios));

    public static async Task<Result<List<RuntimeScenario>>> InitScenarios(
        IGlobalDependency dep,
        StatusContext? consoleStatus,
        IBaseContext baseContext,
        SessionArgs sessionArgs,
        IReadOnlyList<RuntimeScenario> regScenarios)
    {
        try
        {
            var targetScenarios = GetTargetScenarios(sessionArgs, regScenarios);

            TestHostConsole.PrintTargetScenarios(dep, targetScenarios);

            foreach (var scn in targetScenarios)
            {
                if (scn.Init is null) continue;

                dep.LogInfo($"Start init scenario: {scn.ScenarioName}");

                var scnInfo = ScenarioFactory.CreateScenarioInfo(
                    scn.ScenarioName, scn.PlanedDuration, 0, scn.MaxCopiesCount, ScenarioOperation.Init);

                var initScnContext = ScenarioFactory.CreateInitContext(scnInfo, baseContext, scn.CustomSettings);

                if (consoleStatus is not null)
                {
                    consoleStatus.Status = $"Initializing scenario: {ConsoleRender.OkColor(scn.ScenarioName)}";
                    consoleStatus.Refresh();
                }

                await scn.Init(initScnContext).ConfigureAwait(false);
            }

            return Result<List<RuntimeScenario>>.Ok(
                targetScenarios.Select(scenario => scenario with { IsInitialized = true }).ToList());
        }
        catch (Exception ex)
        {
            // Init failing means the target system is not in the state the test assumes,
            // so the run stops rather than producing numbers nobody can trust.
            return Result<List<RuntimeScenario>>.Fail(new ScenarioError.InitScenarioError(ex));
        }
    }

    /// <summary>
    /// Runs each scenario's completion hook with that scenario's final numbers.
    /// </summary>
    /// <remarks>
    /// After the stats are final and before the session returns, so a hook can push a result
    /// somewhere or decide a build has failed. A hook that throws is logged and the rest still
    /// run: one scenario's reporting webhook being down is not a reason to lose the other
    /// scenarios' results.
    /// </remarks>
    public static async Task RunCompletionHooks(
        IGlobalDependency dep,
        IBaseContext baseContext,
        IReadOnlyList<RuntimeScenario> scenarios,
        SessionStats finalStats)
    {
        foreach (var scn in scenarios)
        {
            if (scn.OnCompleted is null) continue;

            var stats = finalStats.ScenarioStats.FirstOrDefault(x => x.ScenarioName == scn.ScenarioName);
            if (stats is null) continue;

            var scnInfo = ScenarioFactory.CreateScenarioInfo(
                scn.ScenarioName, scn.GetExecutedDuration(), 0, scn.MaxCopiesCount, ScenarioOperation.Clean);

            try
            {
                await scn.OnCompleted(ScenarioFactory.CreateCompletionContext(scnInfo, baseContext, stats))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                dep.LogWarn(ex, $"Completion hook failed for scenario: {scn.ScenarioName}");
            }
        }
    }

    /// <summary>Cleans every scenario. A clean that throws is logged and the rest still run.</summary>
    public static async Task CleanScenarios(
        IGlobalDependency dep,
        StatusContext? consoleStatus,
        IBaseContext baseContext,
        IReadOnlyList<RuntimeScenario> scenarios)
    {
        foreach (var scn in scenarios)
        {
            if (scn.Clean is null) continue;

            dep.LogInfo($"Start cleaning scenario: {scn.ScenarioName}");

            if (consoleStatus is not null)
            {
                consoleStatus.Status = $"Cleaning scenario: {ConsoleRender.OkColor(scn.ScenarioName)}";
                consoleStatus.Refresh();
            }

            var scnInfo = ScenarioFactory.CreateScenarioInfo(
                scn.ScenarioName, scn.GetExecutedDuration(), 0, scn.MaxCopiesCount, ScenarioOperation.Clean);

            var cleanScnContext = ScenarioFactory.CreateInitContext(scnInfo, baseContext, scn.CustomSettings);

            try
            {
                await scn.Clean(cleanScnContext).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                dep.LogWarn(ex, $"Cleaning scenario failed: {scn.ScenarioName}");
            }
        }
    }
}
