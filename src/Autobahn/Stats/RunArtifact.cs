using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

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

    /// <summary>
    /// Reads an artifact back, or answers false for anything that is not one.
    /// </summary>
    /// <remarks>
    /// Part of the supported surface rather than an internal helper: this document exists to
    /// be read by something other than the run that wrote it - the UI replays it, run-to-run
    /// comparison consumes it, a CI system asserts against it - and every one of those would
    /// otherwise write its own parser against a shape it does not own.
    ///
    /// False rather than an exception, because the caller is usually looking at a folder of
    /// files it did not choose: a report folder is the user's, and a json file in it is not
    /// necessarily one of these.
    /// </remarks>
    public static bool TryRead(string json, [NotNullWhen(true)] out RunArtifact? artifact)
    {
        artifact = null;

        try
        {
            var read = Internal.Json.AutobahnJson.Deserialize<RunArtifact>(json);

            // A json document with none of the fields this needs deserializes to a default
            // rather than throwing, so the shape is what says whether it is an artifact.
            if (read.SchemaVersion <= 0 || read.Result is null) return false;

            artifact = read;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            // A value of the right name and the wrong type, which System.Text.Json reports
            // this way rather than as a JsonException.
            return false;
        }
    }
}

/// <summary>One scenario's load plan, as it went into the run.</summary>
public sealed record ScenarioPlan
{
    public required string ScenarioName { get; init; }
    public required IReadOnlyList<LoadSimulation> LoadSimulations { get; init; }
}
