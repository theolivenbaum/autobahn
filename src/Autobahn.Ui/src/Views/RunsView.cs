using System.Collections.Generic;
using System.Threading.Tasks;
using Tesserae;
using Autobahn.Ui.Contracts;
using static Tesserae.UI;

namespace Autobahn.Ui.Views
{
    /// <summary>
    /// The runs found beside this one, and a comparison of any two of them.
    /// </summary>
    /// <remarks>
    /// This is the screen that turns Autobahn from "how fast is it" into "did this commit make
    /// it slower", which is why the comparison is a first-class thing here rather than an
    /// export somebody diffs by hand.
    /// </remarks>
    internal static class RunsView
    {
        private static readonly SettableObservable<string> Baseline = SettableObservable.For("");
        private static readonly SettableObservable<string> Candidate = SettableObservable.For("");

        public static IComponent Build(DashboardState state, RunClient client) =>
            Widgets.Screen().Children(
                Defer(async () => await Screen(client), Spinner("Reading the report folder…")));

        private static async Task<IComponent> Screen(RunClient client)
        {
            var runs = await client.LoadRuns();

            if (runs.Length == 0)
            {
                return Message(
                        "No previous runs here",
                        "Runs are read back from the artifacts in the report folder. This one writes its own"
                        + " when it finishes, and comparison needs two.")
                    .Icon(UIcons.Stopwatch).Variant(MessageVariant.Default);
            }

            // Default the comparison to the two most recent, which is the pair somebody almost
            // always wants and saves two clicks before the screen says anything.
            if (Candidate.Value.Length == 0) Candidate.Value = runs[0].Id;
            if (Baseline.Value.Length == 0 && runs.Length > 1) Baseline.Value = runs[1].Id;

            return VStack().Gap(12.px()).WS().Children(
                Widgets.Panel("Runs in this report folder", List(runs)),
                Pickers(runs),
                DeferSync(Baseline, Candidate, (a, b) =>
                    Defer(async () => await Comparison(client, a, b), Spinner("Reading both runs…"))));
        }

        private static IComponent List(PastRunSummary[] runs)
        {
            var rows = VStack().Gap(2.px()).WS();

            rows.Add(HStack().Gap(10.px()).WS().Children(
                TextBlock("Finished").Tiny().SemiBold().W(150.px()).NoShrink(),
                TextBlock("Test").Tiny().SemiBold().Grow(),
                TextBlock("Duration").Tiny().SemiBold().W(90.px()).NoShrink(),
                TextBlock("Ok").Tiny().SemiBold().W(90.px()).NoShrink(),
                TextBlock("Failed").Tiny().SemiBold().W(90.px()).NoShrink(),
                TextBlock("Rps").Tiny().SemiBold().W(90.px()).NoShrink(),
                TextBlock("p95").Tiny().SemiBold().W(90.px()).NoShrink(),
                TextBlock("Gate").Tiny().SemiBold().W(90.px()).NoShrink()));

            for (var i = 0; i < runs.Length; i++)
            {
                var run = runs[i];

                rows.Add(HStack().AlignItemsCenter().Gap(10.px()).WS().Children(
                    TextBlock(Format.DateAndClock(run.CompletedAtEpochMs)).Small().W(150.px()).NoShrink(),
                    Name(run).Grow(),
                    TextBlock(Format.Duration(run.DurationSeconds)).Small().W(90.px()).NoShrink(),
                    TextBlock(Format.Count(run.Ok)).Small().W(90.px()).NoShrink(),
                    Failures(run).W(90.px()).NoShrink(),
                    TextBlock(Format.Rate(run.Rps)).Small().W(90.px()).NoShrink(),
                    TextBlock(Format.Milliseconds(run.P95Ms)).Small().W(90.px()).NoShrink(),
                    Gate(run).W(90.px()).NoShrink()));
            }

            return rows;
        }

        private static IComponent Name(PastRunSummary run)
        {
            var name = TextBlock(run.TestSuite + " · " + run.TestName).Small();

            return run.IsCurrent
                ? (IComponent)HStack().AlignItemsCenter().Gap(6.px()).Children(
                    name, Badge("this run").Pill().Primary())
                : name;
        }

        private static IComponent Failures(PastRunSummary run) =>
            TextBlock(Format.Count(run.Fail)).Small()
                .Foreground(run.Fail > 0 ? Theme.Colors.Red600 : Theme.Secondary.Foreground);

        private static IComponent Gate(PastRunSummary run) =>
            run.ThresholdsPassed == null
                ? (IComponent)TextBlock("ungated").Tiny().Foreground(Theme.Secondary.Foreground)
                : Widgets.Verdict(run.ThresholdsPassed.Value, "passed", "failed");

        private static IComponent Pickers(PastRunSummary[] runs)
        {
            var baseline = Dropdown().Single().W(320.px());
            var candidate = Dropdown().Single().W(320.px());

            for (var i = 0; i < runs.Length; i++)
            {
                var run = runs[i];
                var label = Format.DateAndClock(run.CompletedAtEpochMs) + " · " + run.TestName;

                baseline.AddItems(DropdownItem(label).SetKey(run.Id).SelectedIf(run.Id == Baseline.Value));
                candidate.AddItems(DropdownItem(label).SetKey(run.Id).SelectedIf(run.Id == Candidate.Value));
            }

            baseline.Attach(d => Baseline.Value = Key(d));
            candidate.Attach(d => Candidate.Value = Key(d));

            return Widgets.Panel(
                "Compare",
                HStack().Wrap().AlignItemsCenter().Gap(12.px()).WS().Children(
                    Label("Baseline").Inline().SetContent(baseline),
                    Label("Against").Inline().SetContent(candidate)));
        }

        private static string Key(Dropdown dropdown) =>
            dropdown.SelectedItems.Length == 0 ? "" : dropdown.SelectedItems[0].Key;

        private static async Task<IComponent> Comparison(RunClient client, string baselineId, string candidateId)
        {
            if (baselineId.Length == 0 || candidateId.Length == 0)
                return Widgets.Waiting("Pick two runs to compare.");

            if (baselineId == candidateId)
                return Widgets.Waiting("A run compared with itself has nothing to say. Pick two.");

            var baseline = await client.LoadRun(baselineId);
            var candidate = await client.LoadRun(candidateId);

            if (baseline == null || candidate == null)
                return Widgets.Waiting("One of those runs could not be read back.");

            return Widgets.Panel(
                "Deltas, against the baseline",
                VStack().Gap(2.px()).WS().Children(
                    Header(),
                    Rows(baseline, candidate)));
        }

        private static IComponent Header() =>
            HStack().Gap(10.px()).WS().Children(
                TextBlock("Scenario / step").Tiny().SemiBold().W(260.px()).NoShrink(),
                TextBlock("Measure").Tiny().SemiBold().W(110.px()).NoShrink(),
                TextBlock("Baseline").Tiny().SemiBold().W(110.px()).NoShrink(),
                TextBlock("This run").Tiny().SemiBold().W(110.px()).NoShrink(),
                TextBlock("Change").Tiny().SemiBold().Grow());

        private static IComponent Rows(PastRunDetail baseline, PastRunDetail candidate)
        {
            var rows = VStack().Gap(2.px()).WS();
            var byName = new Dictionary<string, PastScenario>();

            for (var i = 0; i < baseline.Scenarios.Length; i++)
                byName[baseline.Scenarios[i].ScenarioName] = baseline.Scenarios[i];

            for (var i = 0; i < candidate.Scenarios.Length; i++)
            {
                var right = candidate.Scenarios[i];
                PastScenario left;

                if (!byName.TryGetValue(right.ScenarioName, out left))
                {
                    rows.Add(Missing(right.ScenarioName, "only in this run"));
                    continue;
                }

                rows.Add(TextBlock(right.ScenarioName).Small().SemiBold().PT(8));
                rows.Add(Compare("", "requests", left.Ok.Count, right.Ok.Count, false, v => Format.Count(v)));
                rows.Add(Compare("", "failures", left.Fail.Count, right.Fail.Count, true, v => Format.Count(v)));
                rows.Add(Compare("", "rps", left.Ok.Rps, right.Ok.Rps, false, v => Format.Rate(v)));
                rows.Add(Compare("", "p50", left.Ok.P50Ms, right.Ok.P50Ms, true, v => Format.Milliseconds(v)));
                rows.Add(Compare("", "p95", left.Ok.P95Ms, right.Ok.P95Ms, true, v => Format.Milliseconds(v)));
                rows.Add(Compare("", "p99", left.Ok.P99Ms, right.Ok.P99Ms, true, v => Format.Milliseconds(v)));

                rows.Add(Steps(left, right));
            }

            return rows;
        }

        private static IComponent Steps(PastScenario left, PastScenario right)
        {
            var rows = VStack().Gap(2.px()).WS();
            var byName = new Dictionary<string, PastStep>();

            for (var i = 0; i < left.Steps.Length; i++) byName[left.Steps[i].StepName] = left.Steps[i];

            for (var i = 0; i < right.Steps.Length; i++)
            {
                var step = right.Steps[i];
                PastStep before;

                if (!byName.TryGetValue(step.StepName, out before))
                {
                    rows.Add(Missing(step.StepName, "only in this run", indent: true));
                    continue;
                }

                rows.Add(Compare(step.StepName, "p95", before.Ok.P95Ms, step.Ok.P95Ms, true,
                    v => Format.Milliseconds(v), indent: true));
                rows.Add(Compare(step.StepName, "rps", before.Ok.Rps, step.Ok.Rps, false,
                    v => Format.Rate(v), indent: true));
            }

            return rows;
        }

        private static IComponent Missing(string name, string why, bool indent = false) =>
            HStack().Gap(10.px()).WS().Children(
                Name(name, indent),
                TextBlock(why).Tiny().Foreground(Theme.Secondary.Foreground).Grow());

        /// <summary>
        /// The first column, indented for a step so it reads as belonging to the scenario above.
        /// </summary>
        /// <remarks>
        /// A margin rather than leading spaces: this renders as HTML, which collapses them.
        /// </remarks>
        private static IComponent Name(string name, bool indent)
        {
            var text = TextBlock(name).Small().W(260.px()).NoShrink();

            return indent ? text.PL(20) : text;
        }

        /// <summary>
        /// One measure, before and after, with the change coloured by whether it is good news.
        /// </summary>
        /// <remarks>
        /// Which direction is good is per measure and cannot be inferred: more requests is
        /// better, more milliseconds is worse, and a table that coloured every increase green
        /// would be actively misleading about the one number people came to check.
        /// </remarks>
        private static IComponent Compare(
            string name, string measure, double before, double after, bool lowerIsBetter,
            System.Func<double, string> render, bool indent = false)
        {
            var change = after - before;
            var share = before == 0 ? 0 : change / before;

            var text = change == 0
                ? "no change"
                : Format.Delta(change, render) + (before == 0 ? "" : "  (" + Format.Delta(share, Format.Percent) + ")");

            var better = lowerIsBetter ? change < 0 : change > 0;

            var colour = change == 0
                ? Theme.Secondary.Foreground
                : better ? Theme.Colors.Green600 : Theme.Colors.Red600;

            return HStack().AlignItemsCenter().Gap(10.px()).WS().Children(
                Name(name, indent),
                TextBlock(measure).Tiny().Foreground(Theme.Secondary.Foreground).W(110.px()).NoShrink(),
                TextBlock(render(before)).Small().W(110.px()).NoShrink(),
                TextBlock(render(after)).Small().W(110.px()).NoShrink(),
                TextBlock(text).Small().SemiBold().Foreground(colour).Grow());
        }
    }
}
