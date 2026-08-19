using System.Collections.Generic;
using Tesserae;
using Autobahn.Ui.Contracts;
using static Tesserae.UI;

namespace Autobahn.Ui.Views
{
    /// <summary>
    /// Every effective setting and the layer its value came from.
    /// </summary>
    /// <remarks>
    /// This screen answers exactly one question - "why is this value what it is" - and it can
    /// only answer it because the engine records the source as it resolves rather than having
    /// something reconstruct the precedence rules afterwards.
    /// </remarks>
    internal static class ConfigurationView
    {
        public static IComponent Build(DashboardState state) =>
            Widgets.Screen().Children(DeferSync(state.Run, run => Screen(run, state.IsStatic)));

        private static IComponent Screen(RunDescriptor run, bool exported)
        {
            var settings = run.Settings ?? new SettingDescriptor[0];

            var body = VStack().Gap(12.px()).WS().Children(Host(run));

            if (settings.Length == 0)
            {
                body.Add(Message(
                        exported
                            ? "An exported run does not carry its configuration"
                            : "The effective configuration is not in yet",
                        exported
                            ? "The run artifact records what a run measured, not the layers its settings came"
                            + " from. That provenance exists while a run is live, and this page is a copy of"
                            + " one that has finished."
                            : "It is published when the run finishes resolving, just before load starts.")
                    .Icon(UIcons.Settings).Variant(MessageVariant.Default));

                return body;
            }

            body.Add(Widgets.Panel("Effective settings", Table(settings)));

            return body;
        }

        private static IComponent Host(RunDescriptor run)
        {
            var host = run.Host ?? new HostDescriptor();

            return Widgets.Panel(
                "This run",
                VStack().Gap(2.px()).WS().Children(
                    Widgets.Field("Session", run.SessionId),
                    Widgets.Field("Test suite", run.TestSuite),
                    Widgets.Field("Test name", run.TestName),
                    Widgets.Field("Started", Format.DateAndClock(run.StartedAtEpochMs)),
                    Widgets.Field("Reporting interval", Format.Fixed(run.ReportingIntervalSeconds, 0) + " s"),
                    Widgets.Field("Machine", host.MachineName),
                    Widgets.Field("Operating system", host.OperatingSystem),
                    Widgets.Field("Architecture", host.Architecture),
                    Widgets.Field("Cores", Format.Count(host.ProcessorCount)),
                    Widgets.Field("Autobahn", host.AutobahnVersion)));
        }

        private static IComponent Table(SettingDescriptor[] settings)
        {
            var rows = VStack().Gap(2.px()).WS();

            rows.Add(HStack().Gap(10.px()).WS().Children(
                TextBlock("Setting").Tiny().SemiBold().W(220.px()).NoShrink(),
                TextBlock("Value").Tiny().SemiBold().Grow(),
                TextBlock("From").Tiny().SemiBold().W(140.px()).NoShrink()));

            for (var i = 0; i < settings.Length; i++)
            {
                var setting = settings[i];

                rows.Add(HStack().AlignItemsCenter().Gap(10.px()).WS().Children(
                    TextBlock(setting.Name).Small().W(220.px()).NoShrink(),
                    TextBlock(string.IsNullOrEmpty(setting.Value) ? "(empty)" : setting.Value).Small().Grow(),
                    Source(setting.Source).W(140.px()).NoShrink()));
            }

            return rows;
        }

        /// <summary>
        /// The layer a value came from, coloured by how far it is from the default.
        /// </summary>
        /// <remarks>
        /// A value that came from the command line is the one somebody is most likely to be
        /// surprised by, so it is the one that stands out; a default is the one nobody needs to
        /// read, so it recedes.
        /// </remarks>
        private static IComponent Source(string source)
        {
            var badge = Badge(Words(source)).Pill().Outline();

            switch (source)
            {
                case "CommandLine": return badge.Primary();
                case "Environment": return badge.Warning();
                case "JsonConfig": return badge.Info();
                case "Code": return badge.Success();
                default: return badge.Neutral();
            }
        }

        private static string Words(string source)
        {
            switch (source)
            {
                case "CommandLine": return "command line";
                case "Environment": return "environment";
                case "JsonConfig": return "json config";
                case "Code": return "code";
                case "Default": return "default";
                default: return source;
            }
        }
    }
}
