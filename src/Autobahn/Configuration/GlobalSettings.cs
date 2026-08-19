using System.Text.Json.Serialization;
using Autobahn.Internal.Json;
using Autobahn.Stats;
using Autobahn.Thresholds;

namespace Autobahn.Configuration;

/// <summary>The session-wide section of the JSON config.</summary>
public sealed record GlobalSettings
{
    public IReadOnlyList<ScenarioSetting>? ScenariosSettings { get; init; }
    public string? ReportFileName { get; init; }
    public string? ReportFolder { get; init; }
    public IReadOnlyList<ReportFormat>? ReportFormats { get; init; }
    public TimeSpan? ReportingInterval { get; init; }
    public bool? EnableHintsAnalyzer { get; init; }
    public bool? EnableStopTestForcibly { get; init; }

    /// <summary>
    /// Run-wide pass/fail rules. Declaring them here rather than in code is what lets one test
    /// binary be gated differently per environment without a recompile.
    /// </summary>
    public IReadOnlyList<Threshold>? Thresholds { get; init; }

    /// <summary>
    /// Settings every scenario sees, under the same key its own <c>CustomSettings</c> uses.
    /// A scenario's own block wins where the two name the same key.
    /// </summary>
    /// <remarks>
    /// This is where an environment's shared values belong - a base URL, a dataset size, a
    /// tenant id - rather than being repeated in every scenario's block or hard-coded.
    /// </remarks>
    [JsonConverter(typeof(RawJsonConverter))]
    public string? CustomSettings { get; init; }

    public static GlobalSettings Empty { get; } = new();
}
