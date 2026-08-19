namespace Autobahn;

/// <summary>Identifies one running copy of a scenario.</summary>
public sealed record ScenarioInfo
{
    /// <summary>A stable id for this copy, of the form "scenarioName_threadNumber".</summary>
    public required string ThreadId { get; init; }

    /// <summary>This copy's index within the scenario.</summary>
    public required int ThreadNumber { get; init; }

    public required string ScenarioName { get; init; }

    /// <summary>The scenario's planned duration during a run, or its executed duration during clean.</summary>
    public required TimeSpan ScenarioDuration { get; init; }

    public required ScenarioOperation ScenarioOperation { get; init; }
}
