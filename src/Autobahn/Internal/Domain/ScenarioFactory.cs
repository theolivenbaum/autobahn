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
        string scenarioName, TimeSpan duration, int threadNumber, int copyCount, ScenarioOperation operation) => new()
    {
        ThreadId = $"{scenarioName}_{threadNumber}",
        ThreadNumber = threadNumber,
        CopyCount = copyCount,
        ScenarioName = scenarioName,
        ScenarioDuration = duration,
        ScenarioOperation = operation
    };

    public static Result<RuntimeScenario> CreateScenario(ScenarioProps props) => CreateScenario(props, [], 0);

    /// <summary>
    /// Builds a runnable scenario, applying this scenario's share of the combined load when
    /// the run is weighted.
    /// </summary>
    public static Result<RuntimeScenario> CreateScenario(
        ScenarioProps props, IReadOnlyList<LoadSimulation> weighted, int totalWeight)
    {
        var simulations = totalWeight > 0 ? weighted : props.LoadSimulations;

        var plan = SimulationPlan.Create(props.ScenarioName, simulations);
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
            OnCompleted = props.OnCompleted,
            LoadSimulations = plan.Value,
            WarmUpDuration = props.WarmUpDuration,
            PlanedDuration = planedDuration,
            ExecutedDuration = null,
            CustomSettings = string.Empty,
            IsInitialized = false,
            RestartIterationOnFail = props.RestartIterationOnFail,
            MaxFailCount = props.MaxFailCount,
            Weight = props.Weight,
            MaxCopiesCount = SimulationPlan.GetMaxCopiesCount(plan.Value),
            CompletionTimeout = props.CompletionTimeout ?? Constants.DefaultCompletionTimeout,
            IterationTimeout = props.IterationTimeout
        });
    }

    public static Result<List<RuntimeScenario>> CreateScenarios(IReadOnlyList<ScenarioProps> scenarios)
    {
        var checkedScenarios = CheckDuplicateScenarioName(scenarios);
        if (checkedScenarios.IsError) return Result<List<RuntimeScenario>>.Fail(checkedScenarios.Error);

        var weights = CheckWeights(checkedScenarios.Value);
        if (weights.IsError) return Result<List<RuntimeScenario>>.Fail(weights.Error);

        var totalWeight = weights.Value;

        return Result.Sequence(checkedScenarios.Value.Select(props =>
        {
            var weighted = totalWeight > 0
                ? SimulationPlan.ApplyWeight(props.LoadSimulations, props.Weight!.Value, totalWeight)
                : props.LoadSimulations;

            return CreateScenario(props, weighted, totalWeight);
        }));
    }

    /// <summary>
    /// Returns the total weight to divide the combined load by, or zero when the run is
    /// unweighted. A run where only some scenarios declare a weight has no defined total.
    /// </summary>
    internal static Result<int> CheckWeights(IReadOnlyList<ScenarioProps> scenarios)
    {
        var weighted = scenarios.Where(x => x.Weight is not null).ToList();
        if (weighted.Count == 0) return Result<int>.Ok(0);

        var unweighted = scenarios.Where(x => x.Weight is null).ToList();

        if (unweighted.Count > 0)
        {
            return Result<int>.Fail(new ScenarioError.MixedScenarioWeights(
                weighted.Select(x => x.ScenarioName).ToList(),
                unweighted.Select(x => x.ScenarioName).ToList()));
        }

        var invalid = weighted.FirstOrDefault(x => x.Weight!.Value <= 0);

        if (invalid is not null)
            return Result<int>.Fail(new ScenarioError.InvalidScenarioWeight(invalid.ScenarioName, invalid.Weight!.Value));

        return Result<int>.Ok(weighted.Sum(x => x.Weight!.Value));
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
                var plan = SimulationPlan.Create(scenario.ScenarioName, configured);

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
                MaxCopiesCount = SimulationPlan.GetMaxCopiesCount(simulations),
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

    /// <summary>Builds the context a scenario's completion hook receives.</summary>
    public static IScenarioCompletionContext CreateCompletionContext(
        ScenarioInfo scnInfo, IBaseContext context, ScenarioStats stats) =>
        new ScenarioCompletionContext(scnInfo, context, stats);

    private sealed class ScenarioCompletionContext(
        ScenarioInfo scenarioInfo, IBaseContext context, ScenarioStats stats) : IScenarioCompletionContext
    {
        public TestInfo TestInfo => context.TestInfo;
        public ScenarioInfo ScenarioInfo => scenarioInfo;
        public ILogger Logger => context.Logger;
        public ScenarioStats Stats => stats;
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
