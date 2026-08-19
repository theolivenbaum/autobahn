using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Autobahn.Internal.Json;

/// <summary>Reads and writes TimeSpan as "hh:mm:ss", the format the JSON config uses.</summary>
internal sealed class TimeSpanConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();

        if (string.IsNullOrWhiteSpace(text) || !TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value))
            throw new JsonException($"'{text}' is not a valid duration. Expected the format 'hh:mm:ss'.");

        return value;
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("c", CultureInfo.InvariantCulture));
}
