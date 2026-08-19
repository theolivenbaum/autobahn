using System.Text.Json;
using System.Text.Json.Serialization;

namespace Autobahn.Internal.Json;

/// <summary>The serializer settings Autobahn uses for the JSON config and the HTML report view model.</summary>
internal static class AutobahnJson
{
    public static JsonSerializerOptions Config { get; } = Build(indented: false);

    /// <summary>Used for the view model embedded in the HTML report.</summary>
    public static JsonSerializerOptions Report { get; } = Build(indented: false);

    /// <summary>
    /// Used for the run artifact, which is written indented because it is meant to be diffed
    /// in a pull request and read in a CI log as well as parsed.
    /// </summary>
    public static JsonSerializerOptions Artifact { get; } = Build(indented: true);

    private static JsonSerializerOptions Build(bool indented) => new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters =
        {
            new JsonStringEnumConverter(),
            new TimeSpanConverter(),
            new LoadSimulationConverter(),
            new DataSetConverter()
        }
    };

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Config)
        ?? throw new JsonException($"Could not read {typeof(T).Name} from the given JSON.");

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Report);

    public static string SerializeArtifact<T>(T value) => JsonSerializer.Serialize(value, Artifact);
}
