using System.Text.Json.Serialization;
using Autobahn.Internal.Json;

namespace Autobahn.Configuration;

/// <summary>Per-scenario overrides read from the JSON config.</summary>
public sealed record ScenarioSetting
{
    /// <summary>Required: a settings block that names no scenario cannot be applied to one.</summary>
    public required string ScenarioName { get; init; }

    public TimeSpan? WarmUpDuration { get; init; }

    public IReadOnlyList<LoadSimulation>? LoadSimulationsSettings { get; init; }

    /// <summary>Kept as raw JSON so the scenario can bind it to whatever shape it likes.</summary>
    [JsonConverter(typeof(RawJsonConverter))]
    public string? CustomSettings { get; init; }

    public int? MaxFailCount { get; init; }
}
