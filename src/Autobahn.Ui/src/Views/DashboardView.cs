using System.Collections.Generic;
using Tesserae;
using Autobahn.Ui.Contracts;
using static Tesserae.UI;

namespace Autobahn.Ui.Views
{
    /// <summary>
    /// The default screen: what the run is doing right now, and the shape it has been making.
    /// </summary>
    /// <remarks>
    /// The tiles rebuild each interval and the charts do not. A tile is four text nodes, so
    /// rebuilding it is cheaper than wiring it; a chart is an SVG with a point per interval,
    /// and rebuilding one every five seconds would throw away the zoom the reader just set.
    /// The charts therefore bind to the series observables and redraw in place.
    /// </remarks>
    internal static class DashboardView
    {
        public static IComponent Build(DashboardState state) =>
            Widgets.Screen().Children(
                DeferSync(state.Latest, _ => Tiles(state)),
                DeferSync(state.WallClock, wall => Charts(state, wall)),
                DeferSync(state.Latest, _ => StatusCodes(state)),
                LogTail(state));

        private static IComponent Tiles(DashboardState state)
        {
            var latest = state.Latest.Value;
            if (latest == null) return Widgets.Waiting("The first reporting interval has not closed yet.");

            var previous = state.Previous;
            var totals = state.Totals();

            var okRps = DashboardState.Sum(latest, s => s.Ok.Rps);
            var failRps = DashboardState.Sum(latest, s => s.Fail.Rps);
            var p95 = DashboardState.Worst(latest, s => s.Ok.P95Ms);
            var p99 = DashboardState.Worst(latest, s => s.Ok.P99Ms);
            var live = DashboardState.Sum(latest, s => s.ActualCopies);

            return Widgets.Tiles().Children(
                Widgets.Kpi(
                    "requests/sec", Format.Rate(okRps),
                    okRps - DashboardState.Sum(previous, s => s.Ok.Rps), Format.Rate, false,
                    state.Points(f => DashboardState.Sum(f, s => s.Ok.Rps)), Theme.Colors.Green600),

                Widgets.Kpi(
                    "failures/sec", Format.Rate(failRps),
                    failRps - DashboardState.Sum(previous, s => s.Fail.Rps), Format.Rate, true,
                    state.Points(f => DashboardState.Sum(f, s => s.Fail.Rps)), Theme.Colors.Red500),

                Widgets.Kpi(
                    "ok so far", Format.Count(totals.Ok), 0, Format.Count, false,
                    state.Cumulative(f => DashboardState.Sum(f, s => s.Ok.Count)), Theme.Colors.Green600),

                Widgets.Kpi(
                    "failed so far", Format.Count(totals.Fail), 0, Format.Count, true,
                    state.Cumulative(f => DashboardState.Sum(f, s => s.Fail.Count)), Theme.Colors.Red500),

                Widgets.Kpi(
                    "error rate", Format.Percent(totals.ErrorRate), 0, Format.Percent, true,
                    state.Points(Rate), Theme.Colors.Orange600),

                Widgets.Kpi(
                    "p95 latency", Format.Milliseconds(p95),
                    p95 - DashboardState.Worst(previous, s => s.Ok.P95Ms), Format.Milliseconds, true,
                    state.Points(f => DashboardState.Worst(f, s => s.Ok.P95Ms)), Theme.Colors.Orange600),

                Widgets.Kpi(
                    "p99 latency", Format.Milliseconds(p99),
                    p99 - DashboardState.Worst(previous, s => s.Ok.P99Ms), Format.Milliseconds, true,
                    state.Points(f => DashboardState.Worst(f, s => s.Ok.P99Ms)), Theme.Colors.Red600),

                Widgets.Kpi(
                    "live copies", Format.Count(live),
                    live - DashboardState.Sum(previous, s => s.ActualCopies), Format.Count, false,
                    state.Points(f => DashboardState.Sum(f, s => s.ActualCopies)), Theme.Colors.Purple600),

                Widgets.Kpi(
                    "data transferred", Format.Bytes(totals.Bytes), 0, Format.Bytes, false,
                    state.Cumulative(f => DashboardState.Sum(f, s => s.Ok.Bytes + s.Fail.Bytes)),
                    Theme.Colors.Blue600));
        }

        /// <summary>One interval's error rate, which is a ratio and so cannot be accumulated.</summary>
        private static double Rate(LiveFrame frame)
        {
            var ok = DashboardState.Sum(frame, s => s.Ok.Count);
            var fail = DashboardState.Sum(frame, s => s.Fail.Count);

            return ok + fail == 0 ? 0 : fail / (ok + fail);
        }

        private static IComponent Charts(DashboardState state, bool wall)
        {
            var axis = Widgets.TimeAxisTitle(wall);

            return VStack().Gap(12.px()).WS().Children(
                Widgets.ChartPanel(
                    "Throughput",
                    Widgets.Line(wall).Series(state.Throughput).XAxisTitle(axis).YAxisTitle("per second")
                        .FormatValues(v => Format.Rate(v))),

                Widgets.ChartPanel(
                    "Latency percentiles",
                    // Fitted rather than zero-based: a p99 that lives between 180 and 220 ms is
                    // a flat line against a zero baseline, and the whole point is the shape.
                    Widgets.Line(wall, zeroBaseline: false).Series(state.Latency)
                        .XAxisTitle(axis).YAxisTitle("ms").FormatValues(v => Format.Milliseconds(v))),

                Widgets.ChartPanel(
                    "Concurrency: scheduled against actual",
                    Widgets.Line(wall).Series(state.Load).XAxisTitle(axis).YAxisTitle("copies")),

                HStack().Gap(12.px()).WS().Children(
                    Widgets.ChartPanel(
                        "Processor",
                        Widgets.Line(wall).Series(state.Processor).XAxisTitle(axis).YAxisTitle("%"),
                        180).Grow(),
                    Widgets.ChartPanel(
                        "Memory",
                        Widgets.Line(wall).Series(state.Memory).XAxisTitle(axis).YAxisTitle("MB"),
                        180).Grow()),

                HStack().Gap(12.px()).WS().Children(
                    Widgets.ChartPanel(
                        "Thread pool",
                        Widgets.Line(wall).Series(state.ThreadPool).XAxisTitle(axis).YAxisTitle("count"),
                        180).Grow(),
                    Widgets.ChartPanel(
                        "Sockets",
                        Widgets.Line(wall).Series(state.Sockets).XAxisTitle(axis).YAxisTitle("MB"),
                        180).Grow()),

                Widgets.ChartPanel(
                    "Status codes over time",
                    Bars(wall).Series(state.StatusCodes).XAxisTitle(axis).YAxisTitle("responses")));
        }

        /// <summary>A stacked bar chart on the same time axis as everything else on the page.</summary>
        private static BarChart Bars(bool wallClock)
        {
            var chart = BarChart().Stacked().Rounded(1).Legend(ChartLegendPosition.Top);

            return wallClock ? chart.XAxisTime() : chart.FormatXAxis(x => Format.Duration(x));
        }

        private static IComponent StatusCodes(DashboardState state)
        {
            var totals = Totals(state);
            if (totals.Count == 0) return Widgets.Panel("Status codes", TextBlock("Nothing has come back yet.").Small());

            var all = 0;
            for (var i = 0; i < totals.Count; i++) all += totals[i].Count;

            var rows = VStack().Gap(4.px()).WS();

            for (var i = 0; i < totals.Count; i++)
            {
                var row = totals[i];
                var share = all == 0 ? 0 : (double)row.Count / all;

                rows.Add(HStack().AlignItemsCenter().Gap(8.px()).WS().Children(
                    Badge(row.Code).Pill().Tone(row.IsError ? BadgeTone.Danger : BadgeTone.Success).W(90.px()),
                    TextBlock(row.Message).Small().Foreground(Theme.Secondary.Foreground).W(260.px()),
                    ProgressIndicator().Progress((float)(share * 100)).Grow(),
                    TextBlock(Format.Count(row.Count)).Small().SemiBold().W(90.px()),
                    TextBlock(Format.Percent(share)).Small().W(70.px())));
            }

            return Widgets.Panel("Status codes", rows);
        }

        /// <summary>Every status code seen over the whole run, most frequent first.</summary>
        private static List<StatusRow> Totals(DashboardState state)
        {
            var index = new Dictionary<string, StatusRow>();
            var rows = new List<StatusRow>();
            var frames = state.Frames;

            for (var i = 0; i < frames.Count; i++)
            {
                var scenarios = frames[i].Scenarios;
                if (scenarios == null) continue;

                for (var s = 0; s < scenarios.Length; s++)
                {
                    var statuses = scenarios[s].StatusCodes;
                    if (statuses == null) continue;

                    for (var c = 0; c < statuses.Length; c++)
                    {
                        var status = statuses[c];
                        var code = string.IsNullOrEmpty(status.StatusCode) ? status.Message : status.StatusCode;

                        StatusRow row;

                        if (!index.TryGetValue(code, out row))
                        {
                            row = new StatusRow { Code = code, IsError = status.IsError, Message = status.Message };
                            index[code] = row;
                            rows.Add(row);
                        }

                        row.Count += status.Count;
                    }
                }
            }

            rows.Sort((a, b) => b.Count.CompareTo(a.Count));

            return rows;
        }

        /// <summary>
        /// The run's log, tailed.
        /// </summary>
        /// <remarks>
        /// Virtualized, because a chatty run produces more lines than a browser should hold in
        /// the DOM, and filtered by level and by text because the line somebody is looking for
        /// is never the one at the bottom.
        /// </remarks>
        private static IComponent LogTail(DashboardState state)
        {
            var search = SearchBox("Filter the log").SearchAsYouType().WS()
                .OnSearch((_, text) => state.LogSearch.Value = text ?? "");

            Shell.FocusSearch = () => search.Focus();

            var level = Dropdown().Single().W(160.px()).Items(
                DropdownItem("all levels").Selected(),
                DropdownItem("Information"),
                DropdownItem("Warning"),
                DropdownItem("Error"));

            level.Attach(d => state.LogLevel.Value = d.SelectedText == "all levels" ? "" : d.SelectedText);

            var lines = DeferSync(
                state.Logs, state.LogLevel, state.LogSearch,
                (log, wanted, text) => Lines(log, wanted, text));

            return Widgets.Panel(
                "Log",
                VStack().Gap(8.px()).WS().Children(
                    HStack().Gap(8.px()).AlignItemsCenter().WS().Children(search.Grow(), level),
                    lines));
        }

        private static IComponent Lines(IReadOnlyList<LogLine> log, string wanted, string text)
        {
            var matching = new List<IComponent>();
            var needle = (text ?? "").ToLower();

            for (var i = 0; i < log.Count; i++)
            {
                var line = log[i];

                if (wanted.Length > 0 && line.Level != wanted) continue;
                if (needle.Length > 0 && line.Message.ToLower().IndexOf(needle) < 0) continue;

                matching.Add(Line(line));
            }

            return VirtualizedList(rowsPerPage: 12, columnsPerRow: 1)
                .WithListItems(matching)
                .WithEmptyMessage(() => TextBlock("No lines match.").Small().Foreground(Theme.Secondary.Foreground))
                .H(240.px()).WS();
        }

        private static IComponent Line(LogLine line) =>
            HStack().Gap(8.px()).WS().Children(
                TextBlock(Format.Duration(line.ElapsedSeconds)).Tiny()
                    .Foreground(Theme.Secondary.Foreground).W(60.px()).NoShrink(),
                TextBlock(line.Level).Tiny().SemiBold().Foreground(LevelColor(line.Level)).W(90.px()).NoShrink(),
                TextBlock(line.Message).Tiny().Grow());

        private static string LevelColor(string level)
        {
            switch (level)
            {
                case "Critical":
                case "Error": return Theme.Colors.Red600;
                case "Warning": return Theme.Colors.Orange600;
                default: return Theme.Secondary.Foreground;
            }
        }

        private sealed class StatusRow
        {
            public string Code = "";
            public string Message = "";
            public bool IsError;
            public int Count;
        }
    }
}
