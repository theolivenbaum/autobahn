using System.Text;

namespace Autobahn.Internal.Services.Reports;

/// <summary>
/// A very small markdown writer: enough for the report Autobahn generates, and nothing more.
/// </summary>
/// <remarks>
/// The fork point used an F# markdown library for this. Replacing three hundred bytes of
/// formatting with a package that pulls FSharp.Core into every consumer's output was the
/// wrong trade for a C# library.
/// </remarks>
internal sealed class MarkdownDocument
{
    private readonly StringBuilder _sb = new();

    public MarkdownDocument AddText(string text)
    {
        _sb.Append(text);
        return this;
    }

    public MarkdownDocument AddNewline()
    {
        _sb.Append(Environment.NewLine);
        return this;
    }

    /// <summary>A blank line, which is what separates blocks in markdown.</summary>
    public MarkdownDocument AddBlankLine() => AddNewline().AddNewline();

    public MarkdownDocument AddBlockQuote(string text)
    {
        _sb.Append("> ").Append(text);
        return this;
    }

    public MarkdownDocument AddHeader(string header) => AddBlockQuote(header).AddBlankLine();

    public MarkdownDocument AddTable(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (headers.Count == 0)
            return this;

        _sb.Append("| ").Append(string.Join(" | ", headers.Select(Escape))).AppendLine(" |");
        _sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", headers.Count))).AppendLine();

        foreach (var row in rows)
        {
            var cells = Enumerable.Range(0, headers.Count)
                .Select(i => i < row.Count ? Escape(row[i]) : string.Empty);

            _sb.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
        }

        return this;
    }

    /// <summary>A pipe inside a cell would end the cell, so it is escaped.</summary>
    private static string Escape(string cell) => cell.Replace("|", "\\|");

    public static string InlineCode(object? code) => $"`{code}`";

    public override string ToString() => _sb.ToString();
}
