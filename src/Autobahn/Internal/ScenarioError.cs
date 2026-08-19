namespace Autobahn.Internal;

/// <summary>Something wrong with the scenarios the user registered.</summary>
internal abstract record ScenarioError : AppError
{
    public sealed record EmptyRegisteredScenarios : ScenarioError
    {
        public override string Message =>
            "No scenarios were registered. Please use AutobahnRunner.RegisterScenarios(scenarios) to register scenarios";
    }

    public sealed record EmptyScenarioName : ScenarioError
    {
        public override string Message => "Scenario name cannot be empty";
    }

    public sealed record DuplicateScenarioName(IReadOnlyList<string> ScenarioNames) : ScenarioError
    {
        public override string Message =>
            $"Scenario names are not unique: '{string.Join(", ", ScenarioNames)}'";
    }

    public sealed record DuplicateScenarioNamesInConfig(IReadOnlyList<string> ScenarioNames) : ScenarioError
    {
        public override string Message =>
            $"Scenario names are not unique in JSON config: '{string.Join(", ", ScenarioNames)}'";
    }

    public sealed record EmptyScenarioWithEmptyInitAndClean(string ScenarioName) : ScenarioError
    {
        public override string Message =>
            $"Empty scenario: '{ScenarioName}' has no Init and Clean functions defined. "
            + "The empty scenario should have at least Init or Clean functions defined.";
    }

    public sealed record TargetScenariosNotFound(
        IReadOnlyList<string> NotFoundScenarios,
        IReadOnlyList<string> RegisteredScenarios) : ScenarioError
    {
        public override string Message =>
            $"Target scenarios: '{string.Join(", ", NotFoundScenarios)}' are not found. "
            + $"Available scenarios are: '{string.Join(", ", RegisteredScenarios)}'";
    }

    public sealed record InitScenarioError(Exception Exception) : ScenarioError
    {
        public override string Message => $"Init scenario error: '{Exception}'";
    }

    public sealed record CleanScenarioError(Exception Exception) : ScenarioError
    {
        public override string Message => $"Clean scenario error: '{Exception}'";
    }

    public sealed record WarmUpDurationIsBiggerScnDuration(
        string ScenarioName,
        TimeSpan WarmUpDuration,
        TimeSpan ScenarioDuration) : ScenarioError
    {
        public override string Message =>
            $"Scenario '{ScenarioName}' has a warm-up duration '{WarmUpDuration}' that is bigger than "
            + $"the actual scenario's duration '{ScenarioDuration}'. It should be equal or smaller but not bigger.";
    }
}
