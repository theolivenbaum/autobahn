namespace Autobahn.Stats;

/// <summary>
/// The whole run as one versioned, machine-readable document.
/// </summary>
/// <remarks>
/// This is the primary artifact: the UI replays it, run-to-run comparison consumes it, and a
/// CI system asserts against it. The txt, csv, md and html reports are renderings of the same
/// data - a rendering can be reshaped freely, this cannot, which is what
/// <see cref="SchemaVersion"/> is for.
/// </remarks>
public sealed record RunArtifact
{
    /// <summary>
    /// The shape of this document. Bumped when a field is removed or its meaning changes;
    /// adding a field does not bump it, because a reader that ignores unknown fields still
    /// works.
    /// </summary>
    public required int SchemaVersion { get; init; }

    /// <summary>What produced it.</summary>
    public required string Producer { get; init; }

    /// <summary>When the run finished, in UTC.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    public required SessionResult Result { get; init; }

    /// <summary>The load plan each scenario actually ran, so a replay knows what it is looking at.</summary>
    public required IReadOnlyList<ScenarioPlan> Plans { get; init; }
}

/// <summary>One scenario's load plan, as it went into the run.</summary>
public sealed record ScenarioPlan
{
    public required string ScenarioName { get; init; }
    public required IReadOnlyList<LoadSimulation> LoadSimulations { get; init; }
}
