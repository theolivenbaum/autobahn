using System;
using Tesserae;
using Autobahn.Ui.Contracts;
using static Tesserae.UI;

namespace Autobahn.Ui
{
    /// <summary>
    /// The handful of shapes every screen is built from.
    /// </summary>
    /// <remarks>
    /// Here rather than repeated in each view so that a card, a KPI tile and a chart panel look
    /// the same on the dashboard as they do on a scenario's own tab - a dashboard where the
    /// same number is styled two ways is a dashboard people stop trusting.
    /// </remarks>
    internal static class Widgets
    {
        /// <summary>A titled card holding one thing.</summary>
        public static Card Panel(string title, IComponent content) =>
            Card(content).SetTitle(title).WS();

        /// <summary>A titled card holding a chart, which needs a height of its own to fill.</summary>
        public static Card ChartPanel(string title, IComponent chart, int height = 220) =>
            Card(chart.WS().H(height.px())).SetTitle(title).WS();

        /// <summary>
        /// A KPI tile: the number, what it counts, its trend and the shape it has been making.
        /// </summary>
        /// <remarks>
        /// The delta is against the previous interval rather than the run so far, because the
        /// question a live tile answers is "is this getting worse right now".
        /// </remarks>
        public static IComponent Kpi(
            string title, string value, double delta, Func<double, string> render, bool lowerIsBetter,
            double[] spark, string color)
        {
            var tile = Metric(title, value);

            if (spark != null && spark.Length > 1) tile.Chart(Sparkline(spark, height: 34, color: color).WS());

            var text = Format.Delta(delta, render);

            if (text.Length > 0)
            {
                var better = lowerIsBetter ? delta < 0 : delta > 0;

                tile.ChangeInHeader().Change(
                    TextBlock(text).Tiny().SemiBold()
                        .Foreground(better ? Theme.Colors.Green600 : Theme.Colors.Red500));
            }

            return Card(tile).W(210.px()).NoShrink();
        }

        /// <summary>A tile with no trend behind it, for a number that has no previous value.</summary>
        public static IComponent Kpi(string title, string value) =>
            Card(Metric(title, value)).W(210.px()).NoShrink();

        /// <summary>A label and a value, side by side, for the read-only detail lists.</summary>
        public static IComponent Field(string label, string value) =>
            HStack().AlignItemsCenter().Children(
                TextBlock(label).Small().Foreground(Theme.Secondary.Foreground).W(190.px()).NoShrink(),
                TextBlock(string.IsNullOrEmpty(value) ? Format.Absent : value).Small().Grow());

        /// <summary>The run's state as a coloured pill.</summary>
        public static IComponent StatePill(RunState state)
        {
            var badge = Badge(Words(state)).Pill();

            switch (state)
            {
                case RunState.Bombing: return badge.Success();
                case RunState.WarmUp: return badge.Info();
                case RunState.Stopping: return badge.Warning();
                case RunState.Failed: return badge.Danger();
                case RunState.Finished: return badge.Primary();
                default: return badge.Neutral();
            }
        }

        public static string Words(RunState state)
        {
            switch (state)
            {
                case RunState.Init: return "starting";
                case RunState.WarmUp: return "warming up";
                case RunState.Bombing: return "bombing";
                case RunState.Stopping: return "stopping";
                case RunState.Finished: return "finished";
                case RunState.Failed: return "failed";
                default: return "unknown";
            }
        }

        /// <summary>A pass/fail pill, for a threshold or a whole gate.</summary>
        public static IComponent Verdict(bool passing, string passText, string failText) =>
            Badge(passing ? passText : failText).Pill().Filled()
                .Tone(passing ? BadgeTone.Success : BadgeTone.Danger);

        /// <summary>The empty state a screen shows before its first frame arrives.</summary>
        public static IComponent Waiting(string what) =>
            Message("Nothing to show yet", what).Icon(UIcons.Clock).Variant(MessageVariant.Default);

        /// <summary>A heading above a group of panels.</summary>
        public static IComponent Heading(string text) =>
            TextBlock(text).Medium().SemiBold().PT(8);

        /// <summary>A row of tiles that wraps rather than overflowing on a narrow window.</summary>
        public static Stack Tiles() => HStack().Wrap().Gap(12.px()).WS();

        /// <summary>The vertical rhythm every screen's body uses.</summary>
        public static Stack Screen() => VStack().Gap(12.px()).WS().P(16.px());

        /// <summary>
        /// A cartesian chart set up the way every chart on this page is.
        /// </summary>
        /// <remarks>
        /// The time axis is the reason this exists: elapsed-since-start and time-of-day are
        /// different formatters on the same numbers, and having each chart decide for itself
        /// would let two charts on one screen disagree about what x means.
        /// </remarks>
        public static LineChart Line(bool wallClock, bool zeroBaseline = true)
        {
            var chart = LineChart().Legend(ChartLegendPosition.Top).Spikelines().Zoomable().ZeroBaseline(zeroBaseline);

            return wallClock ? chart.XAxisTime() : chart.FormatXAxis(x => Format.Duration(x));
        }

        public static string TimeAxisTitle(bool wallClock) => wallClock ? "time of day" : "elapsed";
    }
}
