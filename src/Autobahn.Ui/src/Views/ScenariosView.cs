using System;
using System.Collections.Generic;
using Tesserae;
using Autobahn.Ui.Contracts;
using static Tesserae.UI;

namespace Autobahn.Ui.Views
{
    /// <summary>
    /// One tab per scenario: its own numbers, its own charts, and the steps inside it.
    /// </summary>
    /// <remarks>
    /// The dashboard's charts sum or take the worst across scenarios, which is the right
    /// summary and the wrong diagnosis. This is where a run with four scenarios stops being one
    /// line and becomes four.
    /// </remarks>
    internal static class ScenariosView
    {
        public static IComponent Build(DashboardState state) =>
            DeferSync(state.Run, run => Tabs(state, run));

        private static IComponent Tabs(DashboardState state, RunDescriptor run)
        {
            if (run.Scenarios == null || run.Scenarios.Length == 0)
                return Widgets.Screen().Children(Widgets.Waiting("The run has not said what it will run yet."));

            var pivot = Pivot().Justified();

            for (var i = 0; i < run.Scenarios.Length; i++)
            {
                var scenario = run.Scenarios[i];

                pivot.Pivot(
                    scenario.ScenarioName,
                    PivotTitle(scenario.ScenarioName),
                    () => Screen(state, scenario),
                    cached: true);
            }

            return pivot.WS();
        }

        private static IComponent Screen(DashboardState state, ScenarioDescriptor scenario)
        {
            var name = scenario.ScenarioName;
            var series = state.Scenario(name);

            return Widgets.Screen().Children(
                DeferSync(state.Latest, _ => Tiles(state, name)),
                DeferSync(state.WallClock, wall => Charts(series, wall)),
                DeferSync(state.Latest, _ => Steps(state, name)),
                DeferSync(state.Latest, frame => Position(scenario, frame, name)));
        }

        private static IComponent Tiles(DashboardState state, string name)
        {
            var latest = state.Latest.Value;
            if (latest == null) return Widgets.Waiting("No interval has closed for this scenario yet.");

            var previous = state.Previous;

            var okRps = DashboardState.Of(latest, name, s => s.Ok.Rps);
            var failRps = DashboardState.Of(latest, name, s => s.Fail.Rps);
            var p95 = DashboardState.Of(latest, name, s => s.Ok.P95Ms);

            var ok = 0;
            var fail = 0;
            var bytes = 0.0;
            var frames = state.Frames;

            for (var i = 0; i < frames.Count; i++)
            {
                ok += (int)DashboardState.Of(frames[i], name, s => s.Ok.Count);
                fail += (int)DashboardState.Of(frames[i], name, s => s.Fail.Count);
                bytes += DashboardState.Of(frames[i], name, s => s.Ok.Bytes + s.Fail.Bytes);
            }

            var all = ok + fail;

            return Widgets.Tiles().Children(
                Widgets.Kpi(
                    "requests/sec", Format.Rate(okRps),
                    okRps - DashboardState.Of(previous, name, s => s.Ok.Rps), Format.Rate, false,
                    state.Points(f => DashboardState.Of(f, name, s => s.Ok.Rps)), Theme.Colors.Green600),

                Widgets.Kpi(
                    "failures/sec", Format.Rate(failRps),
                    failRps - DashboardState.Of(previous, name, s => s.Fail.Rps), Format.Rate, true,
                    state.Points(f => DashboardState.Of(f, name, s => s.Fail.Rps)), Theme.Colors.Red500),

                Widgets.Kpi(
                    "ok so far", Format.Count(ok), 0, Format.Count, false,
                    state.Cumulative(f => DashboardState.Of(f, name, s => s.Ok.Count)), Theme.Colors.Green600),

                Widgets.Kpi(
                    "failed so far", Format.Count(fail), 0, Format.Count, true,
                    state.Cumulative(f => DashboardState.Of(f, name, s => s.Fail.Count)), Theme.Colors.Red500),

                Widgets.Kpi("error rate", Format.Percent(all == 0 ? 0 : (double)fail / all)),

                Widgets.Kpi(
                    "p95 latency", Format.Milliseconds(p95),
                    p95 - DashboardState.Of(previous, name, s => s.Ok.P95Ms), Format.Milliseconds, true,
                    state.Points(f => DashboardState.Of(f, name, s => s.Ok.P95Ms)), Theme.Colors.Orange600),

                Widgets.Kpi(
                    "data transferred", Format.Bytes(bytes), 0, Format.Bytes, false,
                    state.Cumulative(f => DashboardState.Of(f, name, s => s.Ok.Bytes + s.Fail.Bytes)),
                    Theme.Colors.Blue600));
        }

        private static IComponent Charts(ScenarioSeries series, bool wall)
        {
            var axis = Widgets.TimeAxisTitle(wall);

            return VStack().Gap(12.px()).WS().Children(
                Widgets.ChartPanel(
                    "Throughput",
                    Widgets.Line(wall).Series(series.Throughput).XAxisTitle(axis).YAxisTitle("per second"),
                    180),
                Widgets.ChartPanel(
                    "Latency percentiles",
                    Widgets.Line(wall, zeroBaseline: false).Series(series.Latency)
                        .XAxisTitle(axis).YAxisTitle("ms").FormatValues(v => Format.Milliseconds(v)),
                    180),
                Widgets.ChartPanel(
                    "Concurrency: scheduled against actual",
                    Widgets.Line(wall).Series(series.Load).XAxisTitle(axis).YAxisTitle("copies"),
                    180));
        }

        private static IComponent Steps(DashboardState state, string scenarioName)
        {
            var rows = StepRow.Collect(state, scenarioName);

            if (rows.Length == 0)
            {
                return Widgets.Panel(
                    "Steps",
                    TextBlock("This scenario measures itself rather than named steps.")
                        .Small().Foreground(Theme.Secondary.Foreground));
            }

            var list = DetailsList<StepRow>(
                    DetailsListColumn("Step", 200.px(), isRowHeader: true, enableColumnSorting: true, sortingKey: "step"),
                    DetailsListColumn("Ok", 90.px(), enableColumnSorting: true, sortingKey: "ok"),
                    DetailsListColumn("Failed", 90.px(), enableColumnSorting: true, sortingKey: "fail"),
                    DetailsListColumn("Errors", 90.px(), enableColumnSorting: true, sortingKey: "rate"),
                    DetailsListColumn("p50", 90.px(), enableColumnSorting: true, sortingKey: "p50"),
                    DetailsListColumn("p95", 90.px(), enableColumnSorting: true, sortingKey: "p95"),
                    DetailsListColumn("p99", 90.px(), enableColumnSorting: true, sortingKey: "p99"),
                    DetailsListColumn("Data", 100.px(), enableColumnSorting: true, sortingKey: "bytes"),
                    DetailsListColumn("p95 over time", 160.px()))
                .Compact()
                .WithListItems(rows)
                .SortedBy("step")
                .H(Math.Min(360, 60 + rows.Length * 34).px());

            return Widgets.Panel("Steps", list);
        }

        /// <summary>Where this scenario is in its own plan, in words.</summary>
        private static IComponent Position(ScenarioDescriptor scenario, LiveFrame frame, string name)
        {
            var simulation = "not started";
            var scheduled = 0;
            var actual = 0;

            if (frame != null && frame.Scenarios != null)
            {
                for (var i = 0; i < frame.Scenarios.Length; i++)
                {
                    if (frame.Scenarios[i].ScenarioName != name) continue;

                    simulation = frame.Scenarios[i].SimulationName;
                    scheduled = frame.Scenarios[i].ScheduledCopies;
                    actual = frame.Scenarios[i].ActualCopies;
                }
            }

            return Widgets.Panel(
                "Where it is",
                VStack().Gap(4.px()).WS().Children(
                    Widgets.Field("Running now", simulation),
                    Widgets.Field("Scheduled copies", Format.Count(scheduled)),
                    Widgets.Field("Live copies", Format.Count(actual)),
                    Widgets.Field("Most the plan asks for", Format.Count(scenario.MaxCopies)),
                    Widgets.Field(
                        "Planned duration",
                        scenario.PlannedDurationSeconds == null
                            ? "counted in iterations"
                            : Format.Duration(scenario.PlannedDurationSeconds.Value)),
                    Widgets.Field(
                        "Warm-up",
                        scenario.WarmUpDurationSeconds == null
                            ? "none"
                            : Format.Duration(scenario.WarmUpDurationSeconds.Value)),
                    Widgets.Field(
                        "Weight",
                        scenario.Weight == null ? "runs its plan as written" : scenario.Weight.Value + "%")));
        }
    }

    /// <summary>One step's totals over the run so far, as a sortable row.</summary>
    internal sealed class StepRow : IDetailsListItem<StepRow>
    {
        private string _name = "";
        private int _ok;
        private int _fail;
        private double _bytes;
        private double _p50;
        private double _p95;
        private double _p99;
        private double[] _trend = new double[0];

        public bool EnableOnListItemClickEvent => false;

        public void OnListItemClick(int listItemIndex) { }

        /// <summary>
        /// Every step of one scenario, accumulated across the intervals so far.
        /// </summary>
        /// <remarks>
        /// Counts add up over intervals; percentiles do not. The latest interval's percentiles
        /// are shown rather than an average of them, because averaging percentiles produces a
        /// number that is not a percentile of anything.
        /// </remarks>
        public static StepRow[] Collect(DashboardState state, string scenarioName)
        {
            var index = new Dictionary<string, StepRow>();
            var order = new List<StepRow>();
            var trends = new Dictionary<string, List<double>>();

            var frames = state.Frames;

            for (var i = 0; i < frames.Count; i++)
            {
                var scenarios = frames[i].Scenarios;
                if (scenarios == null) continue;

                for (var s = 0; s < scenarios.Length; s++)
                {
                    if (scenarios[s].ScenarioName != scenarioName) continue;

                    var steps = scenarios[s].Steps;
                    if (steps == null) continue;

                    for (var t = 0; t < steps.Length; t++)
                    {
                        var step = steps[t];
                        StepRow row;

                        if (!index.TryGetValue(step.StepName, out row))
                        {
                            row = new StepRow { _name = step.StepName };
                            index[step.StepName] = row;
                            trends[step.StepName] = new List<double>();
                            order.Add(row);
                        }

                        row._ok += step.Ok.Count;
                        row._fail += step.Fail.Count;
                        row._bytes += step.Ok.Bytes + step.Fail.Bytes;
                        row._p50 = step.Ok.P50Ms;
                        row._p95 = step.Ok.P95Ms;
                        row._p99 = step.Ok.P99Ms;

                        trends[step.StepName].Add(step.Ok.P95Ms);
                    }
                }
            }

            for (var i = 0; i < order.Count; i++) order[i]._trend = trends[order[i]._name].ToArray();

            return order.ToArray();
        }

        public int CompareTo(StepRow other, string columnSortingKey)
        {
            switch (columnSortingKey)
            {
                case "ok": return _ok.CompareTo(other._ok);
                case "fail": return _fail.CompareTo(other._fail);
                case "rate": return Rate().CompareTo(other.Rate());
                case "p50": return _p50.CompareTo(other._p50);
                case "p95": return _p95.CompareTo(other._p95);
                case "p99": return _p99.CompareTo(other._p99);
                case "bytes": return _bytes.CompareTo(other._bytes);
                default: return string.Compare(_name, other._name, StringComparison.Ordinal);
            }
        }

        private double Rate() => _ok + _fail == 0 ? 0 : (double)_fail / (_ok + _fail);

        public IEnumerable<IComponent> Render(
            IList<IDetailsListColumn> columns,
            Func<IDetailsListColumn, Func<IComponent>, IComponent> cell)
        {
            yield return cell(columns[0], () => TextBlock(_name).Small());
            yield return cell(columns[1], () => TextBlock(Format.Count(_ok)).Small());
            yield return cell(columns[2], () => Failures());
            yield return cell(columns[3], () => TextBlock(Format.Percent(Rate())).Small());
            yield return cell(columns[4], () => TextBlock(Format.Milliseconds(_p50)).Small());
            yield return cell(columns[5], () => TextBlock(Format.Milliseconds(_p95)).Small());
            yield return cell(columns[6], () => TextBlock(Format.Milliseconds(_p99)).Small());
            yield return cell(columns[7], () => TextBlock(Format.Bytes(_bytes)).Small());
            yield return cell(columns[8], () => Trend());
        }

        private IComponent Failures() =>
            TextBlock(Format.Count(_fail)).Small()
                .Foreground(_fail > 0 ? Theme.Colors.Red600 : Theme.Secondary.Foreground);

        private IComponent Trend() =>
            _trend.Length > 1
                ? (IComponent)Sparkline(_trend, height: 24, color: Theme.Colors.Orange600).WS()
                : TextBlock(Format.Absent).Tiny().Foreground(Theme.Secondary.Foreground);
    }
}
