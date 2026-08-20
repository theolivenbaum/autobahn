using System.Text;

namespace Autobahn.Internal.Services.Reports;

/// <summary>
/// A fixed-width text table, with a rule under every row.
/// </summary>
/// <remarks>
/// The fork point used the ConsoleTables package for this. Autobahn renders it directly:
/// the layout is thirty lines of string building, and the package's alternative-style
/// renderer - the one the text report used - throws a NullReferenceException in its
/// current version. A report format that is one of four shipped outputs should not be able
/// to break because of a formatting dependency.
/// </remarks>
internal sealed class TextTable(params string[] columns)
{
    private readonly List<string[]> _rows = [];

    public TextTable AddRow(params object?[] values)
    {
        if (values.Length != columns.Length)
        {
            throw new ArgumentException(
                $"The table has {columns.Length} columns but the row has {values.Length} values.", nameof(values));
        }

        _rows.Add(values.Select(x => x?.ToString() ?? string.Empty).ToArray());
        return this;
    }

    public override string ToString()
    {
        var widths = columns
            .Select((column, i) => Math.Max(column.Length, _rows.Count == 0 ? 0 : _rows.Max(row => row[i].Length)))
            .ToArray();

        var divider = " " + string.Join("", widths.Select(w => "-".PadRight(w + 3, '-'))) + "- ";

        var sb = new StringBuilder();

        sb.AppendLine(divider);
        AppendRow(sb, columns, widths);
        sb.AppendLine(divider);

        foreach (var row in _rows)
        {
            AppendRow(sb, row, widths);
            sb.AppendLine(divider);
        }

        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells, IReadOnlyList<int> widths)
    {
        sb.Append(" | ");

        for (var i = 0; i < cells.Count; i++)
        {
            sb.Append(cells[i].PadRight(widths[i]));
            sb.Append(" | ");
        }

        sb.AppendLine();
    }
}
