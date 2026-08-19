using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Autobahn.Internal.Json;

/// <summary>
/// Reads a load simulation written as a single-key object whose value is the argument list:
/// <c>{ "KeepConstant": [2, "00:00:02"] }</c>.
/// </summary>
internal sealed class LoadSimulationConverter : JsonConverter<LoadSimulation>
{
    public override LoadSimulation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("A load simulation must be an object of the form { \"KeepConstant\": [2, \"00:00:02\"] }.");

        var cases = root.EnumerateObject().ToArray();
        if (cases.Length != 1)
            throw new JsonException("A load simulation object must carry exactly one simulation name.");

        var name = cases[0].Name;
        var args = cases[0].Value;

        if (args.ValueKind != JsonValueKind.Array)
            throw new JsonException($"Load simulation '{name}' must carry an array of arguments.");

        var values = args.EnumerateArray().ToArray();

        return name switch
        {
            nameof(LoadSimulation.RampingConstant) =>
                new LoadSimulation.RampingConstant(Int(name, values, 0, 2), Duration(name, values, 1, 2)),

            nameof(LoadSimulation.KeepConstant) =>
                new LoadSimulation.KeepConstant(Int(name, values, 0, 2), Duration(name, values, 1, 2)),

            nameof(LoadSimulation.RampingInject) =>
                new LoadSimulation.RampingInject(Int(name, values, 0, 3), Duration(name, values, 1, 3), Duration(name, values, 2, 3)),

            nameof(LoadSimulation.Inject) =>
                new LoadSimulation.Inject(Int(name, values, 0, 3), Duration(name, values, 1, 3), Duration(name, values, 2, 3)),

            nameof(LoadSimulation.InjectRandom) =>
                new LoadSimulation.InjectRandom(Int(name, values, 0, 4), Int(name, values, 1, 4), Duration(name, values, 2, 4), Duration(name, values, 3, 4)),

            nameof(LoadSimulation.IterationsForConstant) =>
                new LoadSimulation.IterationsForConstant(Int(name, values, 0, 2), Int(name, values, 1, 2)),

            nameof(LoadSimulation.IterationsForInject) =>
                new LoadSimulation.IterationsForInject(
                    Int(name, values, 0, 3), Duration(name, values, 1, 3), Int(name, values, 2, 3)),

            nameof(LoadSimulation.Pause) =>
                new LoadSimulation.Pause(Duration(name, values, 0, 1)),

            _ => throw new JsonException($"'{name}' is not a known load simulation.")
        };
    }

    public override void Write(Utf8JsonWriter writer, LoadSimulation value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        switch (value)
        {
            case LoadSimulation.RampingConstant x: WriteCase(writer, nameof(LoadSimulation.RampingConstant), x.Copies, x.During); break;
            case LoadSimulation.KeepConstant x:    WriteCase(writer, nameof(LoadSimulation.KeepConstant), x.Copies, x.During); break;
            case LoadSimulation.RampingInject x:   WriteCase(writer, nameof(LoadSimulation.RampingInject), x.Rate, x.Interval, x.During); break;
            case LoadSimulation.Inject x:          WriteCase(writer, nameof(LoadSimulation.Inject), x.Rate, x.Interval, x.During); break;
            case LoadSimulation.InjectRandom x:    WriteCase(writer, nameof(LoadSimulation.InjectRandom), x.MinRate, x.MaxRate, x.Interval, x.During); break;
            case LoadSimulation.IterationsForConstant x:
                WriteCase(writer, nameof(LoadSimulation.IterationsForConstant), x.Copies, x.Iterations); break;
            case LoadSimulation.IterationsForInject x:
                WriteCase(writer, nameof(LoadSimulation.IterationsForInject), x.Rate, x.Interval, x.Iterations); break;
            case LoadSimulation.Pause x:           WriteCase(writer, nameof(LoadSimulation.Pause), x.During); break;
            default: throw new JsonException($"Unknown load simulation: {value.GetType().Name}");
        }

        writer.WriteEndObject();
    }

    private static void WriteCase(Utf8JsonWriter writer, string name, params object[] args)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();

        foreach (var arg in args)
        {
            switch (arg)
            {
                case int i: writer.WriteNumberValue(i); break;
                case TimeSpan t: writer.WriteStringValue(t.ToString("c", CultureInfo.InvariantCulture)); break;
                default: throw new JsonException($"Cannot write load simulation argument of type {arg.GetType().Name}");
            }
        }

        writer.WriteEndArray();
    }

    private static void Expect(string name, JsonElement[] values, int count)
    {
        if (values.Length != count)
            throw new JsonException($"Load simulation '{name}' expects {count} arguments but got {values.Length}.");
    }

    private static int Int(string name, JsonElement[] values, int index, int count)
    {
        Expect(name, values, count);

        if (values[index].ValueKind != JsonValueKind.Number)
            throw new JsonException($"Argument {index} of load simulation '{name}' must be a number.");

        return values[index].GetInt32();
    }

    private static TimeSpan Duration(string name, JsonElement[] values, int index, int count)
    {
        Expect(name, values, count);

        var text = values[index].ValueKind == JsonValueKind.String ? values[index].GetString() : null;

        if (string.IsNullOrWhiteSpace(text) || !TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value))
            throw new JsonException($"Argument {index} of load simulation '{name}' must be a duration like \"00:00:30\".");

        return value;
    }
}
