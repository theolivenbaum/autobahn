using Tesserae;
using static Tesserae.UI;
using static Transpose.Core.dom;

namespace Autobahn.Ui
{
    /// <summary>The live view of a running load test.</summary>
    internal static class App
    {
        private static void Main()
        {
            // The shell owns the scrolling: the rail and the run's header stay put while the
            // screen under them moves.
            document.body.style.overflow = "hidden";

            if (window.matchMedia("(prefers-color-scheme: dark)").matches) Theme.Dark();

            var state = new DashboardState();
            var client = new RunClient(state);

            MountToBody(new Shell(state, client).Build());

            client.Start();
        }
    }
}
