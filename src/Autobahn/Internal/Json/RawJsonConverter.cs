using System.Text.Json;
using System.Text.Json.Serialization;

namespace Autobahn.Internal.Json;

/// <summary>
/// Keeps a config section as its raw JSON text instead of binding it. Used for
/// CustomSettings, whose shape belongs to the scenario author, not to Autobahn.
/// </summary>
internal sealed class RawJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();

        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            writer.WriteNullValue();
            return;
        }

        using var doc = JsonDocument.Parse(value);
        doc.RootElement.WriteTo(writer);
    }
}
