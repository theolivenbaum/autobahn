using System.Threading.Tasks;
using Tesserae;
using Autobahn.Ui.Contracts;
using static Tesserae.UI;
using static Transpose.Core.dom;

namespace Autobahn.Ui.Views
{
    /// <summary>
    /// The artifacts the run wrote, with a preview of the ones worth reading in place.
    /// </summary>
    /// <remarks>
    /// The html report is offered as a link rather than an inline frame: this page's content
    /// policy allows nothing to be embedded, deliberately, and relaxing it so a report could be
    /// framed would relax it for everything else too.
    /// </remarks>
    internal static class ReportsView
    {
        public static IComponent Build(DashboardState state, RunClient client) =>
            Widgets.Screen().Children(
                DeferSync(state.Reports, reports => Screen(reports, client, state.IsStatic)));

        private static IComponent Screen(ReportDescriptor[] reports, RunClient client, bool exported)
        {
            if (reports == null || reports.Length == 0)
            {
                return Message(
                        "No reports yet",
                        "They are written when the run finishes - including when it is stopped early.")
                    .Icon(UIcons.Document).Variant(MessageVariant.Default);
            }

            var body = VStack().Gap(10.px()).WS();

            // An exported page may be anywhere by now, and the run's other files are not: they
            // are named so somebody knows what the run produced, and nothing more.
            if (exported)
            {
                body.Add(TextBlock("The files the run wrote, beside its artifact. This exported page is not one of them.")
                    .Small().Foreground(Theme.Secondary.Foreground));
            }

            for (var i = 0; i < reports.Length; i++) body.Add(Report(reports[i], client, exported));

            return body;
        }

        private static IComponent Report(ReportDescriptor report, RunClient client, bool exported)
        {
            var header = HStack().AlignItemsCenter().Gap(10.px()).WS().Children(
                Badge(report.Format.ToLower()).Pill().Info().W(70.px()).NoShrink(),
                TextBlock(report.FileName).Small().SemiBold().Grow(),
                TextBlock(Format.Bytes(report.SizeBytes)).Tiny()
                    .Foreground(Theme.Secondary.Foreground).NoShrink());

            if (!exported)
            {
                var url = client.ReportUrl(report.FileName);
                header.Add(Button("Open").SetIcon(UIcons.Download).OnClick(() => window.open(url, "_blank")));
            }

            var card = Card(VStack().Gap(8.px()).WS().Children(header)).WS();

            if (!exported && Previewable(report.Format))
            {
                card.SetFooter(
                    Defer(
                        async () => await Preview(client, report.FileName),
                        Spinner("Reading " + report.FileName + "…")));
            }

            return card;
        }

        /// <summary>
        /// Whether this format reads as text.
        /// </summary>
        /// <remarks>
        /// The html report is a whole document with its own styles and scripts; dropping it
        /// into this page would be pasting one application inside another.
        /// </remarks>
        private static bool Previewable(string format) =>
            format == "Txt" || format == "Md" || format == "Csv" || format == "Json";

        private static async Task<IComponent> Preview(RunClient client, string fileName)
        {
            var body = await client.LoadReport(fileName);
            if (body == null) return TextBlock("Could not read that report.").Small();

            // Long enough to see the shape of it, short enough not to make the page the report.
            // The whole thing is one click away.
            var head = body.Length > 20000
                ? body.Substring(0, 20000) + "\n… (truncated - open it to read the rest)"
                : body;

            return TextBlock(head).Tiny()
                .Style(css =>
                {
                    css.whiteSpace = "pre";
                    css.overflowX = "auto";
                    css.maxHeight = "320px";
                    css.overflowY = "auto";
                    css.fontFamily = Theme.Fonts.Monospace;
                })
                .WS();
        }
    }
}
