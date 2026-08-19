using System.Collections.Generic;
using Tesserae;
using static Tesserae.UI;

namespace Autobahn.Ui.Views
{
    /// <summary>
    /// What failed, how often, and - the part a total cannot say - when.
    /// </summary>
    /// <remarks>
    /// A burst of errors confined to one thirty second window is a different problem from a
    /// steady two percent, and a table of counts flattens one into the other. The strip beside
    /// each group is there to keep them apart.
    /// </remarks>
    internal static class ErrorsView
    {
        public static IComponent Build(DashboardState state)
        {
            var search = SearchBox("Filter failures").SearchAsYouType().W(320.px())
                .OnSearch((_, text) => state.ErrorSearch.Value = text ?? "");

            Shell.FocusSearch = () => search.Focus();

            return Widgets.Screen().Children(
                HStack().AlignItemsCenter().Gap(8.px()).WS().Children(search),
                DeferSync(state.Errors, state.ErrorSearch, (groups, text) => Groups(state, groups, text)));
        }

        private static IComponent Groups(DashboardState state, ErrorGroup[] groups, string text)
        {
            var needle = (text ?? "").ToLower();
            var matching = new List<ErrorGroup>();

            for (var i = 0; i < groups.Length; i++)
            {
                if (needle.Length > 0
                    && groups[i].Describe().ToLower().IndexOf(needle) < 0
                    && groups[i].Message.ToLower().IndexOf(needle) < 0
                    && groups[i].ScenarioName.ToLower().IndexOf(needle) < 0) continue;

                matching.Add(groups[i]);
            }

            if (matching.Count == 0)
            {
                return groups.Length == 0
                    ? Message("Nothing has failed", "Every response so far has been a success.")
                        .Icon(UIcons.CheckCircle).Variant(MessageVariant.Success)
                    : Message("No failures match", "Nothing in this run matches that filter.")
                        .Icon(UIcons.Search).Variant(MessageVariant.Default);
            }

            var last = Last(state);
            var body = VStack().Gap(8.px()).WS();

            for (var i = 0; i < matching.Count; i++) body.Add(Group(matching[i], last));

            return body;
        }

        private static IComponent Group(ErrorGroup group, double last)
        {
            var header = HStack().AlignItemsCenter().Gap(10.px()).WS().Children(
                Badge(group.Describe()).Pill().Danger().NoShrink(),
                TextBlock(group.ScenarioName).Small().SemiBold().NoShrink(),
                TextBlock(group.Message).Small().Foreground(Theme.Secondary.Foreground).Grow(),
                TextBlock(Format.Count(group.Count)).Small().SemiBold().NoShrink(),
                TextBlock(Format.Percent(group.Share)).Small().Foreground(Theme.Secondary.Foreground).NoShrink());

            var when = HStack().AlignItemsCenter().Gap(10.px()).WS().Children(
                TextBlock("first at " + Format.Duration(group.FirstSeenSeconds)).Tiny()
                    .Foreground(Theme.Secondary.Foreground),
                TextBlock("last at " + Format.Duration(group.LastSeenSeconds)).Tiny()
                    .Foreground(Theme.Secondary.Foreground));

            return Card(VStack().Gap(6.px()).WS().Children(header, Strip(group, last), when)).WS();
        }

        /// <summary>
        /// One bar per reporting interval of the whole run, lit where this group was active.
        /// </summary>
        /// <remarks>
        /// Drawn against the run's full span rather than the group's own, so two groups can be
        /// compared by looking at them: bars that only covered the intervals a group appeared in
        /// would make a thirty-second burst and a run-long trickle the same picture.
        /// </remarks>
        private static IComponent Strip(ErrorGroup group, double last)
        {
            if (last <= 0) return TextBlock("").Tiny();

            var active = new HashSet<double>();
            for (var i = 0; i < group.Intervals.Count; i++) active.Add(group.Intervals[i]);

            var bars = new List<(UptimeStatus, IComponent)>();
            var seen = new List<double>(group.Intervals);
            seen.Sort();

            foreach (var interval in Timeline(group, last))
            {
                var lit = active.Contains(interval);

                bars.Add((
                    lit ? UptimeStatus.Major : UptimeStatus.Operational,
                    TextBlock((lit ? "failing at " : "clean at ") + Format.Duration(interval)).Tiny()));
            }

            return UptimeBars().Compact().Items(bars).WS();
        }

        /// <summary>The intervals of the run, taken from the ones this group knows about.</summary>
        private static IEnumerable<double> Timeline(ErrorGroup group, double last)
        {
            var step = group.Intervals.Count > 1
                ? group.Intervals[1] - group.Intervals[0]
                : group.FirstSeenSeconds;

            if (step <= 0) step = last;

            // A very long run has more intervals than a strip has room for; the bars would be
            // sub-pixel and say nothing, so the step widens instead.
            var count = (int)(last / step);
            if (count > 120) step = last / 120;

            for (var at = step; at <= last + 0.001; at += step) yield return Nearest(group, at, step);
        }

        /// <summary>
        /// Snaps a position onto an interval this group actually recorded.
        /// </summary>
        /// <remarks>
        /// The elapsed seconds a frame carries are the scheduler's, not a multiple of anything,
        /// so a strip built from arithmetic would never land on one and every bar would read
        /// clean.
        /// </remarks>
        private static double Nearest(ErrorGroup group, double at, double step)
        {
            for (var i = 0; i < group.Intervals.Count; i++)
            {
                if (System.Math.Abs(group.Intervals[i] - at) <= step / 2) return group.Intervals[i];
            }

            return at;
        }

        private static double Last(DashboardState state)
        {
            var frames = state.Frames;

            return frames.Count == 0 ? 0 : frames[frames.Count - 1].ElapsedSeconds;
        }
    }
}
