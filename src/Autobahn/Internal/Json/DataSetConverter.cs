using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Autobahn.Internal.Json;

/// <summary>
/// Writes a plugin's <see cref="DataSet"/> in the shape the HTML report's script expects:
/// tables keyed by name, each with its columns (name plus caption) and its rows as objects.
/// </summary>
/// <remarks>Write-only: nothing reads a DataSet back out of the report.</remarks>
internal sealed class DataSetConverter : JsonConverter<DataSet>
{
    public override DataSet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Reading a DataSet from JSON is not supported.");

    public override void Write(Utf8JsonWriter writer, DataSet value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("Tables");
        writer.WriteStartObject();

        foreach (DataTable table in value.Tables)
        {
            writer.WritePropertyName(table.TableName);
            WriteTable(writer, table, options);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteTable(Utf8JsonWriter writer, DataTable table, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("TableName", table.TableName);

        writer.WritePropertyName("Columns");
        writer.WriteStartArray();

        foreach (DataColumn column in table.Columns)
        {
            writer.WriteStartObject();
            writer.WriteString("ColumnName", column.ColumnName);
            writer.WriteString("Caption", string.IsNullOrEmpty(column.Caption) ? column.ColumnName : column.Caption);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("Rows");
        writer.WriteStartArray();

        foreach (DataRow row in table.Rows)
        {
            writer.WriteStartObject();

            foreach (DataColumn column in table.Columns)
            {
                writer.WritePropertyName(column.ColumnName);
                var cell = row[column];

                if (cell is null or DBNull) writer.WriteNullValue();
                else JsonSerializer.Serialize(writer, cell, cell.GetType(), options);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
