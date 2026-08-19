using Spectre.Console;
using Spectre.Console.Rendering;

namespace Autobahn.Internal.Infra;

/// <summary>The Spectre.Console vocabulary the console report and the live table are written in.</summary>
internal static class ConsoleRender
{
    public static string EscapeMarkup(string text) => Markup.Escape(text);

    public static void Render(IRenderable renderable) => AnsiConsole.Write(renderable);

    /// <summary>
    /// Gives the console a usable width when Autobahn is not attached to a terminal.
    /// </summary>
    /// <remarks>
    /// With no terminal to measure, Spectre falls back to a width that collapses every
    /// table to an ellipsis - which is exactly the case that matters, because that output
    /// is the CI log somebody has to read afterwards. Rendering the report as plain lines
    /// rather than as tables is the proper fix and is TODO.md section 5.
    /// </remarks>
    public static void UseFixedWidth(int width)
    {
        AnsiConsole.Profile.Width = width;
        AnsiConsole.Profile.Height = int.MaxValue;
    }

    public static string OkColor(object? text) => $"[lime]{text}[/]";
    public static string OkEscColor(object? text) => OkColor(EscapeMarkup(text?.ToString() ?? ""));

    public static string WarningColor(object? text) => $"[yellow]{text}[/]";
    public static string WarningEscColor(object? text) => WarningColor(EscapeMarkup(text?.ToString() ?? ""));

    public static string ErrorColor(object? text) => $"[red]{text}[/]";
    public static string ErrorEscColor(object? text) => ErrorColor(EscapeMarkup(text?.ToString() ?? ""));

    public static string BlueColor(object? text) => $"[deepskyblue1]{text}[/]";
    public static string BlueEscColor(object? text) => BlueColor(EscapeMarkup(text?.ToString() ?? ""));

    public static string Plain(object? text) => text?.ToString() ?? "";

    public static IRenderable AddLine(string text) => new Markup($"{text}{Environment.NewLine}");

    public static IRenderable AddLogo(string logo) => new FigletText(logo) { Color = Color.Red };

    public static IRenderable AddHeader(string header) => new Rule(header).Centered();

    public static List<IRenderable> AddList(IEnumerable<IEnumerable<string>> items)
    {
        var result = new List<IRenderable>();
        var index = 0;

        foreach (var group in items)
        {
            if (index > 0) result.Add(AddLine(string.Empty));

            foreach (var renderable in group)
            {
                result.Add(new Markup(renderable));
                result.Add(AddLine(string.Empty));
            }

            index++;
        }

        return result;
    }

    public static IRenderable AddTable(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var table = new Table { Border = TableBorder.Square };

        for (var i = 0; i < headers.Count; i++)
        {
            var col = new TableColumn(headers[i]);

            if (i == 0) col.RightAligned();
            else if (i == headers.Count - 1) col.LeftAligned();
            else col.Centered();

            table.AddColumn(col);
        }

        foreach (var row in rows)
            table.AddRow(row.Select(col => (IRenderable)new Markup(col)));

        return table;
    }
}
