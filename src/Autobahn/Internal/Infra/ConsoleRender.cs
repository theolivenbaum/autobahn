using Spectre.Console;
using Spectre.Console.Rendering;

namespace Autobahn.Internal.Infra;

/// <summary>The Spectre.Console vocabulary the console report and the live table are written in.</summary>
internal static class ConsoleRender
{
    private static readonly Lock Sync = new();
    private static readonly List<Action> Deferred = [];

    private static bool _liveDisplayActive;

    public static string EscapeMarkup(string text) => Markup.Escape(text);

    /// <summary>
    /// Draws something to the console, or holds it until the live table comes down. The final
    /// report goes through here too: it is written the moment the run ends, which can be
    /// before the live display has finished its last frame.
    /// </summary>
    public static void Render(IRenderable renderable) => WriteOrDefer(() => AnsiConsole.Write(renderable));

    /// <summary>
    /// Marks the live table as owning the terminal, so nothing else writes over it.
    /// </summary>
    /// <remarks>
    /// Spectre's live display redraws in place and has no way to let another writer put a line
    /// above it; a log line written while it is up lands in the middle of the table and the
    /// next redraw leaves the wreckage behind. So console writes are held while the table is
    /// up and replayed the moment it comes down, in order. The file log is untouched
    /// throughout - nothing is lost, it is only deferred on the one surface that cannot take it.
    /// </remarks>
    public static void BeginLiveDisplay()
    {
        lock (Sync) _liveDisplayActive = true;
    }

    /// <summary>Releases the terminal and replays whatever was held while the table was up.</summary>
    public static void EndLiveDisplay()
    {
        Action[] deferred;

        lock (Sync)
        {
            _liveDisplayActive = false;
            deferred = [.. Deferred];
            Deferred.Clear();
        }

        foreach (var write in deferred) write();
    }

    /// <summary>
    /// Writes to the console now, or holds the write until the live table comes down.
    /// </summary>
    public static void WriteOrDefer(Action write)
    {
        lock (Sync)
        {
            if (_liveDisplayActive)
            {
                Deferred.Add(write);
                return;
            }
        }

        write();
    }

    /// <summary>
    /// Gives the console a usable width when Autobahn is not attached to a terminal.
    /// </summary>
    /// <remarks>
    /// With no terminal to measure, Spectre falls back to a width that collapses every
    /// table to an ellipsis - which is exactly the case that matters, because that output
    /// is the CI log somebody has to read afterwards. The live table is not drawn at all
    /// there; interval progress goes out as plain log lines instead.
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
