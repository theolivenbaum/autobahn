using Tesserae;
using Autobahn.Ui.Contracts;
using static Tesserae.UI;

namespace Autobahn.Ui.Views
{
    /// <summary>
    /// The load plan each scenario will run, as a timeline with a playhead on it.
    /// </summary>
    /// <remarks>
    /// Drawn from the descriptor, which exists before any load does, so the plan can be
    /// sanity-checked before firing as well as followed while it runs. A ramp is drawn as a
    /// ramp rather than a step, which is the whole reason each segment carries where it came
    /// from as well as where it is going.
    /// </remarks>
    internal static class PlanView
    {
        public static IComponent Build(DashboardState state) =>
            Widgets.Screen().Children(
                DeferSync(state.Run, state.Latest, (run, frame) => Screen(run, frame)));

        private static IComponent Screen(RunDescriptor run, LiveFrame frame)
        {
            if (run.Scenarios == null || run.Scenarios.Length == 0)
                return Widgets.Waiting("The run has not said what it will run yet.");

            var elapsed = frame == null ? 0 : frame.ElapsedSeconds;
            var body = VStack().Gap(12.px()).WS();

            for (var i = 0; i < run.Scenarios.Length; i++) body.Add(Scenario(run.Scenarios[i], elapsed));

            return body;
        }

        private static IComponent Scenario(ScenarioDescriptor scenario, double elapsed)
        {
            var plan = scenario.Plan ?? new SimulationSegment[0];

            if (plan.Length == 0)
            {
                return Widgets.Panel(
                    scenario.ScenarioName,
                    TextBlock("This scenario declares no load simulations.").Small());
            }

            return Widgets.Panel(
                scenario.ScenarioName,
                VStack().Gap(10.px()).WS().Children(
                    Curve(plan, elapsed),
                    Segments(plan, elapsed)));
        }

        /// <summary>
        /// The concurrency (or rate) the plan asks for, over the plan's own timeline.
        /// </summary>
        /// <remarks>
        /// Two points per segment - where it starts and where it ends - which is what makes a
        /// ramp slope and a hold flat without the chart having to know what either word means.
        /// A counted segment has no length, so it is drawn as an instant: the plan genuinely
        /// cannot say how long it will take.
        /// </remarks>
        private static IComponent Curve(SimulationSegment[] plan, double elapsed)
        {
            var x = new double[plan.Length * 2];
            var y = new double[plan.Length * 2];

            var at = 0.0;

            for (var i = 0; i < plan.Length; i++)
            {
                var segment = plan[i];
                var length = segment.DurationSeconds == null ? 0 : segment.DurationSeconds.Value;

                x[i * 2] = at;
                y[i * 2] = segment.FromLevel;

                at += length;

                x[i * 2 + 1] = at;
                y[i * 2 + 1] = segment.Level;
            }

            var planned = new ChartSeries("planned", x, y, Theme.Colors.Blue600);

            // The playhead: a two-point series standing where the run has got to, so it is
            // drawn on the same scale as the plan rather than positioned over it by arithmetic
            // that would go wrong the moment the chart resized.
            //
            // Both series go in one call: Series(params ChartSeries[]) replaces what the chart
            // holds rather than appending, so a second call would leave the plan out and the
            // axis would collapse onto the playhead's single position.
            var series = elapsed > 0 && at > 0
                ? new[]
                {
                    planned,
                    new ChartSeries("now", new[] { elapsed, elapsed }, new[] { 0.0, Highest(y) }, Theme.Colors.Red500)
                    {
                        LineWidth = 1,
                        FillOpacity = 0
                    }
                }
                : new[] { planned };

            return AreaChart()
                .Series(series)
                .FormatXAxis(v => Format.Duration(v))
                .XAxisTitle("elapsed")
                .YAxisTitle("copies or rate")
                .Legend(false)
                .WS().H(180.px());
        }

        private static double Highest(double[] values)
        {
            var highest = 0.0;
            for (var i = 0; i < values.Length; i++) if (values[i] > highest) highest = values[i];

            return highest;
        }

        private static IComponent Segments(SimulationSegment[] plan, double elapsed)
        {
            var rows = VStack().Gap(2.px()).WS();

            for (var i = 0; i < plan.Length; i++)
            {
                var segment = plan[i];
                var ends = segment.StartSeconds + (segment.DurationSeconds == null ? 0 : segment.DurationSeconds.Value);
                var here = elapsed >= segment.StartSeconds && (elapsed < ends || segment.DurationSeconds == null);

                var when = segment.DurationSeconds == null
                    ? "from " + Format.Duration(segment.StartSeconds)
                    : Format.Duration(segment.StartSeconds) + " – " + Format.Duration(ends);

                rows.Add(HStack().AlignItemsCenter().Gap(10.px()).WS().Children(
                    Badge(segment.Kind).Pill().Tone(here ? BadgeTone.Primary : BadgeTone.Neutral).W(180.px()).NoShrink(),
                    TextBlock(segment.Label).Small().Grow(),
                    TextBlock(when).Tiny().Foreground(Theme.Secondary.Foreground).W(150.px()).NoShrink(),
                    TextBlock(segment.Iterations == null ? "" : segment.Iterations.Value + " iterations").Tiny()
                        .Foreground(Theme.Secondary.Foreground).W(120.px()).NoShrink()));
            }

            return rows;
        }
    }
}
