using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Autobahn.Configuration;
using Autobahn.Stats;

namespace Autobahn.Internal.Domain;

/// <summary>Validates the scenarios a user registered and turns them into runnable ones.</summary>
internal static class ScenarioFactory
{
    public static Result<ScenarioProps> CheckEmptyScenarioName(ScenarioProps scenario) =>
        string.IsNullOrWhiteSpace(scenario.ScenarioName)
            ? Result<ScenarioProps>.Fail(new ScenarioError.EmptyScenarioName())
            : Result<ScenarioProps>.Ok(scenario);

    public static Result<IReadOnlyList<ScenarioProps>> CheckDuplicateScenarioName(IReadOnlyList<ScenarioProps> scenarios)
    {
        var duplicates = scenarios.Select(x => x.ScenarioName).FilterDuplicates().ToList();

        return duplicates.Count > 0
            ? Result<IReadOnlyList<ScenarioProps>>.Fail(new ScenarioError.DuplicateScenarioName(duplicates))
            : Result<IReadOnlyList<ScenarioProps>>.Ok(scenarios);
    }

    /// <summary>An empty scenario earns its place only if it initializes or cleans something.</summary>
    public static Result<ScenarioProps> CheckInitOnlyScenario(ScenarioProps scenario)
    {
        if (scenario.Run is not null) return Result<ScenarioProps>.Ok(scenario);

        return scenario.Init is not null || scenario.Clean is not null
            ? Result<ScenarioProps>.Ok(scenario)
            : Result<ScenarioProps>.Fail(new ScenarioError.EmptyScenarioWithEmptyInitAndClean(scenario.ScenarioName));
    }

    public static Result<ScenarioProps> CheckWarmUpDuration(TimeSpan scnDuration, ScenarioProps scenario)
    {
        if (scenario.WarmUpDuration is not { } warmUpDuration) return Result<ScenarioProps>.Ok(scenario);

        return scnDuration < warmUpDuration
            ? Result<ScenarioProps>.Fail(new ScenarioError.WarmUpDurationIsBiggerScnDuration(
                scenario.ScenarioName, warmUpDuration, scnDuration))
            : Result<ScenarioProps>.Ok(scenario);
    }

    public static Result<ScenarioProps> Validate(ScenarioProps scenario, TimeSpan scnDuration) =>
        CheckEmptyScenarioName(scenario)
            .Bind(CheckInitOnlyScenario)
            .Bind(x => CheckWarmUpDuration(scnDuration, x));

    public static ScenarioInfo CreateScenarioInfo(
        string scenarioName, TimeSpan duration, int threadNumber, ScenarioOperation operation) => new()
    {
        ThreadId = $"{scenarioName}_{threadNumber}",
        ThreadNumber = threadNumber,
        ScenarioName = scenarioName,
        ScenarioDuration = duration,
        ScenarioOperation = operation
    };

    public static Result<RuntimeScenario> CreateScenario(ScenarioProps props)
    {
        var plan = SimulationPlan.Create(props.LoadSimulations);
        if (plan.IsError) return Result<RuntimeScenario>.Fail(plan.Error);

        var planedDuration = SimulationPlan.GetPlanedDuration(plan.Value);

        var validated = Validate(props, planedDuration);
        if (validated.IsError) return Result<RuntimeScenario>.Fail(validated.Error);

        return Result<RuntimeScenario>.Ok(new RuntimeScenario
        {
            ScenarioName = props.ScenarioName,
            Init = props.Init,
            Clean = props.Clean,
            Run = props.Run,
            LoadSimulations = plan.Value,
            WarmUpDuration = props.WarmUpDuration,
            PlanedDuration = planedDuration,
            ExecutedDuration = null,
            CustomSettings = string.Empty,
            IsInitialized = false,
            RestartIterationOnFail = props.RestartIterationOnFail,
            MaxFailCount = props.MaxFailCount
        });
    }

    public static Result<List<RuntimeScenario>> CreateScenarios(IReadOnlyList<ScenarioProps> scenarios)
    {
        var checkedScenarios = CheckDuplicateScenarioName(scenarios);
        if (checkedScenarios.IsError) return Result<List<RuntimeScenario>>.Fail(checkedScenarios.Error);

        return Result.Sequence(checkedScenarios.Value.Select(CreateScenario));
    }

    public static List<RuntimeScenario> FilterTargetScenarios(
        IReadOnlyList<string> targetScenarios, IEnumerable<RuntimeScenario> scenarios) =>
        scenarios.Where(x => targetScenarios.Contains(x.ScenarioName)).ToList();

    /// <summary>Overlays the JSON config's per-scenario settings onto the scenarios that match by name.</summary>
    public static List<RuntimeScenario> ApplySettings(
        IReadOnlyList<ScenarioSetting> settings, IEnumerable<RuntimeScenario> scenarios)
    {
        return scenarios.Select(scenario =>
        {
            var setting = settings.FirstOrDefault(x => x.ScenarioName == scenario.ScenarioName);
            return setting is null ? scenario : Apply(scenario, setting);
        }).ToList();

        static RuntimeScenario Apply(RuntimeScenario scenario, ScenarioSetting settings)
        {
            var simulations = scenario.LoadSimulations;

            if (settings.LoadSimulationsSettings is { } configured)
            {
                var plan = SimulationPlan.Create(configured);

                // The config's simulations were validated when the config was read; a failure
                // here would mean the config model and the validator disagree.
                if (plan.IsError) throw new InvalidOperationException(plan.Error.Message);

                simulations = plan.Value;
            }

            return scenario with
            {
                LoadSimulations = simulations,
                WarmUpDuration = settings.WarmUpDuration,
                PlanedDuration = SimulationPlan.GetPlanedDuration(simulations),
                CustomSettings = settings.CustomSettings ?? "",
                MaxFailCount = settings.MaxFailCount ?? Constants.ScenarioMaxFailCount
            };
        }
    }

    public static RuntimeScenario SetExecutedDuration(RuntimeScenario scenario, TimeSpan executedDuration) =>
        executedDuration < scenario.PlanedDuration
            ? scenario with { ExecutedDuration = executedDuration }
            : scenario with { ExecutedDuration = scenario.PlanedDuration };

    /// <summary>Replaces each target scenario with the finished copy that knows its executed duration.</summary>
    public static List<RuntimeScenario> UpdateExecutedDuration(
        IEnumerable<RuntimeScenario> targetScenarios, IReadOnlyList<RuntimeScenario> finishedScenarios) =>
        targetScenarios
            .Select(scn => finishedScenarios.FirstOrDefault(x => x.ScenarioName == scn.ScenarioName) ?? scn)
            .ToList();

    public static List<RuntimeScenario> GetScenariosForWarmUp(IEnumerable<RuntimeScenario> scenarios) =>
        scenarios.Where(x => x.Run is not null && x.WarmUpDuration is not null).ToList();

    public static List<RuntimeScenario> GetScenariosForBombing(IEnumerable<RuntimeScenario> scenarios) =>
        scenarios.Where(x => x.Run is not null).ToList();

    public static TimeSpan GetMaxDuration(IEnumerable<RuntimeScenario> scenarios) =>
        scenarios.Max(x => x.PlanedDuration);

    public static TimeSpan GetMaxWarmUpDuration(IEnumerable<RuntimeScenario> scenarios) =>
        scenarios.Where(x => x.WarmUpDuration is not null).Max(x => x.WarmUpDuration!.Value);

    /// <summary>Builds the context a scenario's init and clean functions receive.</summary>
    public static IScenarioInitContext CreateInitContext(
        ScenarioInfo scnInfo, IBaseContext context, string customSettings) =>
        new ScenarioInitContext(scnInfo, context, ParseCustomSettings(customSettings));

    private static IConfiguration ParseCustomSettings(string settings)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(settings));
            return new ConfigurationBuilder().AddJsonStream(stream).Build();
        }
        catch
        {
            // A scenario with no custom settings, or with settings that are not valid JSON,
            // gets an empty configuration rather than a failed run.
            return new ConfigurationBuilder().Build();
        }
    }

    private sealed class ScenarioInitContext(
        ScenarioInfo scenarioInfo, IBaseContext context, IConfiguration customSettings) : IScenarioInitContext
    {
        public TestInfo TestInfo => context.TestInfo;
        public ScenarioInfo ScenarioInfo => scenarioInfo;
        public HostInfo HostInfo => context.GetHostInfo();
        public IConfiguration CustomSettings => customSettings;
        public ILogger Logger => context.Logger;
    }
}
