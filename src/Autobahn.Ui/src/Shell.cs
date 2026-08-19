using System;
using Tesserae;
using Autobahn.Ui.Contracts;
using Autobahn.Ui.Views;
using static Tesserae.UI;
using static Transpose.Core.dom;

namespace Autobahn.Ui
{
    /// <summary>
    /// The application: a rail of sections down the left, the run's header across the top, and
    /// one screen at a time under it.
    /// </summary>
    /// <remarks>
    /// The header belongs to the shell rather than to the dashboard on purpose. Which screen is
    /// open is a question about what somebody is reading; whether the run is still going, how
    /// far through it is and how to stop it are questions they need answered from every screen.
    /// </remarks>
    internal sealed class Shell
    {
        /// <summary>
        /// What the "/" key focuses, set by whichever screen currently owns a search box.
        /// </summary>
        /// <remarks>
        /// A field rather than a search box in the shell, because the thing worth searching is
        /// different per screen - the log on the dashboard, the failures on the errors screen -
        /// and one box in the header would have to mean all of them or none.
        /// </remarks>
        public static Action FocusSearch;

        // Short labels, long tooltips: the rail is a narrow column and ellipsizes anything
        // much past eight characters, and "Thresho…" is worse than a word that fits.
        private static readonly Section[] Sections =
        {
            new Section("dashboard", "Live", "Live dashboard", UIcons.Dashboard),
            new Section("scenarios", "Scenarios", "Scenarios and their steps", UIcons.Layers),
            new Section("errors", "Errors", "Failures, grouped", UIcons.Bug),
            new Section("thresholds", "Gates", "Thresholds and the run's verdict", UIcons.ShieldCheck),
            new Section("plan", "Plan", "The load plan", UIcons.ChartGantt),
            new Section("configuration", "Config", "The effective configuration", UIcons.Settings),
            new Section("runs", "Runs", "Previous runs, and comparison", UIcons.Stopwatch),
            new Section("reports", "Reports", "The artifacts this run wrote", UIcons.Document)
        };

        private readonly DashboardState _state;
        private readonly RunClient _client;
        private readonly SettableObservable<string> _section = SettableObservable.For("dashboard");

        public Shell(DashboardState state, RunClient client)
        {
            _state = state;
            _client = client;
        }

        public IComponent Build()
        {
            var rail = Rail();

            var content = DeferSync(_section, section => Screen(section))
                .WS().Grow()
                .Style(css => css.overflowY = "auto");

            BindKeyboard();

            return HStack().S().NoWrap().Children(
                rail.HS().NoShrink(),
                VStack().Grow().HS().NoWrap().Children(
                    Header(),
                    content));
        }

        private Sidenav Rail()
        {
            var rail = Sidenav();

            rail.AddHeader(new SidenavButton("brand", UIcons.Road, "Autobahn").AsBrand());

            for (var i = 0; i < Sections.Length; i++)
            {
                var section = Sections[i];
                var button = new SidenavButton(section.Id, section.Icon, section.Title)
                    .Tooltip(section.Tooltip + "  (" + (i + 1) + ")");

                if (section.Id == _section.Value) button.Selected();

                button.OnClick(() =>
                {
                    rail.Select(section.Id);
                    _section.Value = section.Id;
                });

                rail.AddContent(button);
            }

            // The rail is the only thing that knows which button is which, so a jump from the
            // keyboard has to go through it or the selection and the screen drift apart.
            _section.ObserveFutureChanges(id => rail.Select(id));

            return rail;
        }

        private IComponent Header()
        {
            var clock = Toggle("Wall clock", "Elapsed")
                .OnChange((s, _) => _state.WallClock.Value = s.IsChecked);

            var row = HStack().AlignItemsCenter().Gap(10.px()).WS().Children(
                DeferSync(_state.Run, run => Title(run)),
                DeferSync(_state.Latest, frame => Widgets.StatePill(frame == null ? RunState.Init : frame.State)),
                DeferSync(_state.Connected, connected =>
                    Badge(connected ? "connected" : "reconnecting").Pill()
                        .Tone(connected ? BadgeTone.Success : BadgeTone.Warning)),
                VStack().Grow(),
                clock,
                Toggle("Live", "Paused").Checked().OnChange((s, _) => _state.SetPaused(!s.IsChecked)),
                Button("Stop run").Danger().SetIcon(UIcons.Stop).OnClick(() => Confirm(false)),
                Button("Stop now").Danger().Compact().SetIcon(UIcons.Bolt).OnClick(() => Confirm(true)));

            return VStack().Gap(6.px()).WS().NoShrink()
                .P(16.px())
                .Style(css => css.borderBottom = "1px solid " + Theme.Default.Border)
                .Children(
                    row,
                    DeferSync(_state.Run, _state.Latest, (run, frame) => Progress(run, frame)));
        }

        private static IComponent Title(RunDescriptor run)
        {
            var name = string.IsNullOrEmpty(run.TestName) ? "Autobahn" : run.TestName;
            var suite = string.IsNullOrEmpty(run.TestSuite) ? "" : run.TestSuite + " · ";

            return VStack().Children(
                TextBlock(name).Medium().SemiBold(),
                TextBlock(suite + Host(run)).Tiny().Foreground(Theme.Secondary.Foreground));
        }

        private static string Host(RunDescriptor run)
        {
            var host = run.Host;
            if (host == null || string.IsNullOrEmpty(host.MachineName)) return "";

            return host.MachineName + " · " + host.ProcessorCount + " cores · Autobahn " + host.AutobahnVersion;
        }

        /// <summary>Where the run is, in words and as a bar when the plan knows how long it is.</summary>
        private IComponent Progress(RunDescriptor run, LiveFrame frame)
        {
            var elapsed = frame == null ? 0 : frame.ElapsedSeconds;
            var status = frame == null ? Starting(run) : frame.StatusText;

            var line = HStack().AlignItemsCenter().Gap(10.px()).WS().Children(
                LiveProgress(status).Grow(),
                TextBlock(Format.Duration(elapsed) + Planned(run)).Tiny()
                    .Foreground(Theme.Secondary.Foreground).NoShrink());

            if (run.PlannedDurationSeconds == null) return line;

            var share = run.PlannedDurationSeconds.Value <= 0
                ? 0
                : elapsed / run.PlannedDurationSeconds.Value * 100;

            return VStack().Gap(4.px()).WS().Children(
                line,
                ProgressIndicator().Progress((float)(share > 100 ? 100 : share)).WS());
        }

        /// <summary>
        /// What to say before the first interval, which is not nothing.
        /// </summary>
        /// <remarks>
        /// Warm-up produces no frames by design, so a run with a thirty second warm-up would
        /// otherwise show an empty dashboard and no explanation for half a minute - which reads
        /// as broken rather than as busy.
        /// </remarks>
        private static string Starting(RunDescriptor run)
        {
            if (run.Scenarios == null) return "Starting…";

            for (var i = 0; i < run.Scenarios.Length; i++)
            {
                if (run.Scenarios[i].WarmUpDurationSeconds != null)
                    return "Warming up. The first measured interval follows the warm-up.";
            }

            return "Starting…";
        }

        private static string Planned(RunDescriptor run) =>
            run.PlannedDurationSeconds == null
                ? " elapsed"
                : " of " + Format.Duration(run.PlannedDurationSeconds.Value);

        private IComponent Screen(string section)
        {
            switch (section)
            {
                case "scenarios": return ScenariosView.Build(_state);
                case "errors": return ErrorsView.Build(_state);
                case "thresholds": return ThresholdsView.Build(_state);
                case "plan": return PlanView.Build(_state);
                case "configuration": return ConfigurationView.Build(_state);
                case "runs": return RunsView.Build(_state, _client);
                case "reports": return ReportsView.Build(_state, _client);
                default: return DashboardView.Build(_state);
            }
        }

        /// <summary>Asks before stopping, because a stop cannot be taken back.</summary>
        private void Confirm(bool force)
        {
            var question = force
                ? "Stop the run immediately? In-flight iterations are abandoned; the reports are still written."
                : "Stop the run? The current iterations finish first and the reports are written.";

            Ask(question, force);
        }

        private async void Ask(string question, bool force)
        {
            var answer = await Dialog("Stop this run", question).YesNoAsync();
            if (answer != Tesserae.Dialog.Response.Yes) return;

            var result = await _client.RequestStop(force);

            if (result.Accepted) Toast().Information("Stopping", result.Message);
            else Toast().Warning("Not stopped", result.Message);
        }

        /// <summary>
        /// Number keys jump between sections, "." freezes the live view and "/" finds the box.
        /// </summary>
        /// <remarks>
        /// Skipped whenever the caret is in something that takes text, or typing a "." into the
        /// log filter would pause the run's own charts.
        /// </remarks>
        private void BindKeyboard() => window.addEventListener("keydown", (System.Action<Event>)(raw =>
        {
            var e = raw.As<KeyboardEvent>();
            if (e.ctrlKey || e.altKey || e.metaKey || IsTyping()) return;

            if (e.key == ".")
            {
                _state.SetPaused(!_state.Paused.Value);
                return;
            }

            if (e.key == "/")
            {
                if (FocusSearch == null) return;

                e.preventDefault();
                FocusSearch();
                return;
            }

            for (var i = 0; i < Sections.Length; i++)
            {
                if (e.key != (i + 1).ToString()) continue;

                _section.Value = Sections[i].Id;
                return;
            }
        }));

        private static bool IsTyping()
        {
            var active = document.activeElement;
            if (active == null) return false;

            var tag = active.tagName.ToLower();

            return tag == "input" || tag == "textarea" || tag == "select";
        }

        private sealed class Section
        {
            public Section(string id, string title, string tooltip, UIcons icon)
            {
                Id = id;
                Title = title;
                Tooltip = tooltip;
                Icon = icon;
            }

            public string Id { get; }
            public string Title { get; }
            public string Tooltip { get; }
            public UIcons Icon { get; }
        }
    }
}
