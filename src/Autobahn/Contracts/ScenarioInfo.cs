namespace Autobahn;

/// <summary>Identifies one running copy of a scenario.</summary>
public sealed record ScenarioInfo
{
    /// <summary>A stable id for this copy, of the form "scenarioName_threadNumber".</summary>
    public required string ThreadId { get; init; }

    /// <summary>This copy's index within the scenario, counting from zero.</summary>
    public required int ThreadNumber { get; init; }

    /// <summary>
    /// The most copies this scenario's load plan will ever run at once.
    /// </summary>
    /// <remarks>
    /// Taken from the plan, not from how many happen to be alive right now, so it does not
    /// move while the load ramps. That is what makes it usable as the denominator when a
    /// scenario partitions a dataset across its copies - see
    /// <c>IScenarioContext.OwnsIndex</c>.
    /// </remarks>
    public required int CopyCount { get; init; }

    public required string ScenarioName { get; init; }

    /// <summary>The scenario's planned duration during a run, or its executed duration during clean.</summary>
    public required TimeSpan ScenarioDuration { get; init; }

    public required ScenarioOperation ScenarioOperation { get; init; }
}
