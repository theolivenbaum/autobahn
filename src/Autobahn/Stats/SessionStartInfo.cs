using Autobahn.Configuration;
using Autobahn.Thresholds;

namespace Autobahn.Stats;

/// <summary>
/// What the run turned out to be, once everything was resolved and before any load was
/// generated.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="SessionResult"/>: one says what happened, this says what was
/// going to be attempted. It exists because the interesting settings are the *resolved* ones -
/// a caller holding an <see cref="AutobahnContext"/> knows what it asked for, not what the
/// JSON config, the environment and the command line did to it afterwards.
/// </remarks>
public sealed record SessionStartInfo
{
    public required TestInfo TestInfo { get; init; }
    public required HostInfo HostInfo { get; init; }

    public required TimeSpan ReportingInterval { get; init; }

    /// <summary>Where this run's reports will be written.</summary>
    public required string ReportFolder { get; init; }

    /// <summary>Each effective setting and the layer its value came from.</summary>
    public required IReadOnlyList<EffectiveSetting> EffectiveSettings { get; init; }

    /// <summary>The pass/fail rules this run is gated on, before any of them has been checked.</summary>
    public required IReadOnlyList<Threshold> Thresholds { get; init; }

    /// <summary>The scenarios that will actually run, with the plans they will actually run.</summary>
    public required IReadOnlyList<ScenarioStartInfo> Scenarios { get; init; }
}

/// <summary>One scenario as it will be run, after targeting, weighting and validation.</summary>
public sealed record ScenarioStartInfo
{
    public required string ScenarioName { get; init; }

    /// <summary>
    /// The load plan as it will run, which is not always the one that was written: a weighted
    /// scenario's plan is rescaled to its share of the combined load.
    /// </summary>
    public required IReadOnlyList<LoadSimulation> LoadSimulations { get; init; }

    /// <summary>Null when a counted segment makes the length unknowable in advance.</summary>
    public required TimeSpan? PlannedDuration { get; init; }

    public required TimeSpan? WarmUpDuration { get; init; }

    /// <summary>The most copies this plan ever runs at once.</summary>
    public required int MaxCopies { get; init; }

    /// <summary>This scenario's share of the combined load, or null when it runs its plan as written.</summary>
    public int? Weight { get; init; }
}
