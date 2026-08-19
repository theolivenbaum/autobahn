using System.Collections.Generic;
using Tesserae;
using Autobahn.Ui.Contracts;
using static Tesserae.UI;

namespace Autobahn.Ui.Views
{
    /// <summary>
    /// The run's own pass/fail rules, and whether it is passing them.
    /// </summary>
    /// <remarks>
    /// The gate verdict is at the top and is deliberately the largest thing on the screen: it
    /// is the number CI will act on, and everything under it is the explanation.
    /// </remarks>
    internal static class ThresholdsView
    {
        public static IComponent Build(DashboardState state) =>
            Widgets.Screen().Children(
                DeferSync(state.Run, state.Thresholds, (run, live) => Screen(state, run, live)));

        private static IComponent Screen(DashboardState state, RunDescriptor run, ThresholdFrame[] live)
        {
            var declared = run.Thresholds ?? new ThresholdDescriptor[0];

            if (declared.Length == 0 && live.Length == 0)
            {
                return Message(
                        "This run is not gated",
                        "No thresholds were declared, so the run cannot fail on its own numbers.")
                    .Icon(UIcons.ShieldCheck).Variant(MessageVariant.Default);
            }

            var body = VStack().Gap(10.px()).WS();

            body.Add(Verdict(live));

            for (var i = 0; i < declared.Length; i++) body.Add(Rule(state, declared[i], Match(live, declared[i])));

            // A rule the host reported but the descriptor does not carry: the descriptor was
            // taken before the run resolved, so showing the live one is better than hiding it.
            for (var i = 0; i < live.Length; i++)
            {
                if (!Declared(declared, live[i])) body.Add(Rule(state, null, live[i]));
            }

            return body;
        }

        private static IComponent Verdict(ThresholdFrame[] live)
        {
            var checkedCount = 0;
            var failing = 0;
            var aborted = false;

            for (var i = 0; i < live.Length; i++)
            {
                if (!live[i].Checked) continue;

                checkedCount++;
                if (!live[i].Passing) failing++;
                if (live[i].Aborted) aborted = true;
            }

            var passing = failing == 0;

            var text = checkedCount == 0
                ? "No rule has been checked yet."
                : passing
                    ? "All " + checkedCount + " checked rule(s) are passing."
                    : failing + " of " + checkedCount + " checked rule(s) are failing.";

            if (aborted) text += " One of them has ended the run.";

            return Card(HStack().AlignItemsCenter().Gap(12.px()).WS().Children(
                TextBlock(checkedCount == 0 ? "pending" : passing ? "passing" : "failing")
                    .XXLarge().Bold()
                    .Foreground(checkedCount == 0
                        ? Theme.Secondary.Foreground
                        : passing ? Theme.Colors.Green600 : Theme.Colors.Red600),
                TextBlock(text).Small().Grow())).WS();
        }

        private static IComponent Rule(DashboardState state, ThresholdDescriptor declared, ThresholdFrame live)
        {
            var name = declared != null ? declared.Name : live != null ? live.Name : "";
            var scenario = declared != null ? declared.ScenarioName : live != null ? live.ScenarioName : "";

            var header = HStack().AlignItemsCenter().Gap(10.px()).WS().Children(
                TextBlock(name).Small().SemiBold().Grow(),
                Status(live));

            var facts = VStack().Gap(2.px()).WS();

            if (declared != null)
            {
                facts.Add(Widgets.Field("Asserts", declared.Subject + " " + declared.Comparison + " " + declared.Target));
                facts.Add(Widgets.Field("Scope", declared.Scope));
                facts.Add(Widgets.Field(
                    "Starts checking",
                    declared.StartsAfterSeconds == null
                        ? "immediately"
                        : Format.Duration(declared.StartsAfterSeconds.Value) + " into the run"));
                facts.Add(Widgets.Field(
                    "Ends the run",
                    declared.AbortAfter == null
                        ? "no - advisory until the end"
                        : "after " + declared.AbortAfter.Value + " consecutive failures"));
            }

            if (!string.IsNullOrEmpty(scenario)) facts.Add(Widgets.Field("Scenario", scenario));

            if (live != null)
            {
                facts.Add(Widgets.Field("Observed", live.Checked ? Format.Fixed(live.Observed, 2) : "not checked yet"));
                facts.Add(Widgets.Field("Checks", live.FailedChecks + " failed of " + live.TotalChecks));
            }

            var body = VStack().Gap(6.px()).WS().Children(header, facts);

            if (live != null) body.Add(Strip(state, live));

            return Card(body).WS();
        }

        private static IComponent Status(ThresholdFrame live)
        {
            if (live == null) return Badge("not checked").Pill().Neutral();
            if (!live.Checked) return Badge("waiting").Pill().Neutral();

            return Widgets.Verdict(live.Passing, "passing", "failing");
        }

        /// <summary>
        /// Every interval this rule was checked in, pass or fail.
        /// </summary>
        /// <remarks>
        /// The reason the frames carry threshold states at all: a rule that passed, failed for
        /// a minute and recovered reads at a glance from this strip and not at all from its
        /// final verdict.
        /// </remarks>
        private static IComponent Strip(DashboardState state, ThresholdFrame live)
        {
            var history = state.ThresholdHistory(live);
            if (history.Length == 0) return TextBlock("").Tiny();

            var bars = new List<(UptimeStatus, IComponent)>();
            var frames = state.Frames;

            for (var i = 0; i < history.Length; i++)
            {
                var at = i < frames.Count ? frames[i].ElapsedSeconds : 0;

                var status = !history[i].Checked
                    ? UptimeStatus.None
                    : history[i].Passing ? UptimeStatus.Operational : UptimeStatus.Major;

                bars.Add((
                    status,
                    TextBlock(
                        Format.Duration(at) + " · "
                        + (history[i].Checked ? Format.Fixed(history[i].Observed, 2) : "not checked")).Tiny()));
            }

            return UptimeBars().Compact().Items(bars).WS();
        }

        private static ThresholdFrame Match(ThresholdFrame[] live, ThresholdDescriptor declared)
        {
            for (var i = 0; i < live.Length; i++)
            {
                if (live[i].Name == declared.Name && live[i].ScenarioName == declared.ScenarioName) return live[i];
            }

            // A scenario-scoped rule is tallied per scenario, so one declaration can produce
            // several live rows; the name alone is the fallback match.
            for (var i = 0; i < live.Length; i++)
            {
                if (live[i].Name == declared.Name) return live[i];
            }

            return null;
        }

        private static bool Declared(ThresholdDescriptor[] declared, ThresholdFrame live)
        {
            for (var i = 0; i < declared.Length; i++)
            {
                if (declared[i].Name == live.Name) return true;
            }

            return false;
        }
    }
}
