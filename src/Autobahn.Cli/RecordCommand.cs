using Autobahn.Http;
using Microsoft.Playwright;

namespace Autobahn.Cli;

/// <summary>
/// Learns a scenario by watching a real browser session.
/// </summary>
/// <remarks>
/// A browser is opened, you use the site the way a user would, and every request the page
/// makes is recorded. When the browser closes, the requests become C# source: a scenario you
/// own and edit, driven by an HTTP client.
///
/// Deliberately *not* browser-driven load testing. Running browsers under load makes the
/// generator the bottleneck and measures the generator - a machine that can drive twenty
/// browsers cannot tell you what a service does at two thousand users. Learning from one
/// browser session and then hammering with an HTTP client measures the service, which is the
/// thing being asked about.
///
/// What it produces is a starting point and says so in its own header: a recording carries one
/// session's ids, one user's tokens and one moment's data, and those have to become feeds
/// before the test means anything.
/// </remarks>
internal static class RecordCommand
{
    public static async Task<int> Run(CliOptions options)
    {
        var url = options.Source!;

        if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
            throw new AutobahnException($"'{url}' is not a URL to open. Try 'autobahn record https://example.com'.");

        var recorded = await Capture(url, options).ConfigureAwait(false);

        if (recorded.Count == 0)
        {
            throw new AutobahnException(
                "The session made no requests worth recording. Static assets are filtered out by default; "
                + "pass --include-assets to keep them.");
        }

        var code = ScenarioCodeGenerator.Generate(recorded, CodeOptions(url, options));
        var outputPath = options.ReportFileName ?? DefaultFileName(url, options);

        await File.WriteAllTextAsync(outputPath, code).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"Recorded {recorded.Count} request(s) into {outputPath}.");
        Console.WriteLine("Read the header before running it: the ids and tokens in there are one session's.");

        return AutobahnExitCode.Ok;
    }

    private static async Task<IReadOnlyList<HttpRequest>> Capture(string url, CliOptions options)
    {
        using var playwright = await CreatePlaywright().ConfigureAwait(false);

        await using var browser = await Launch(playwright, options).ConfigureAwait(false);

        var page = await browser.NewPageAsync().ConfigureAwait(false);
        var recorder = new RequestRecorder(url, options);

        page.Request += (_, request) => recorder.Observe(request);

        Console.WriteLine($"Opening {url}.");
        Console.WriteLine(options.Headless
            ? "Recording the page load. Nothing to click - this browser has no window."
            : "Use the site as a user would. Close the browser window when you are done.");
        Console.WriteLine();

        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle })
            .ConfigureAwait(false);

        if (!options.Headless) await WaitForClose(page).ConfigureAwait(false);

        return recorder.Requests;
    }

    /// <summary>
    /// Launches Chromium, preferring one the machine already has.
    /// </summary>
    /// <remarks>
    /// <c>AUTOBAHN_BROWSER_PATH</c> and <c>--browser-path</c> exist because CI images and dev
    /// containers usually carry a browser already, and it is rarely the exact build the
    /// Playwright package pins - which otherwise fails with a message about a missing
    /// executable rather than about a version.
    /// </remarks>
    private static async Task<IBrowser> Launch(IPlaywright playwright, CliOptions options)
    {
        var launch = new BrowserTypeLaunchOptions { Headless = options.Headless };

        var executable = options.BrowserPath ?? Environment.GetEnvironmentVariable("AUTOBAHN_BROWSER_PATH");

        if (executable is { Length: > 0 })
        {
            if (!File.Exists(executable))
                throw new AutobahnException($"No browser at '{executable}'.");

            launch.ExecutablePath = executable;
        }

        try
        {
            return await playwright.Chromium.LaunchAsync(launch).ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            throw new AutobahnException(
                "Chromium would not start. Install Playwright's browsers once with "
                + "'playwright install chromium', or point --browser-path at one this machine "
                + $"already has. ({ex.Message.Split('\n')[0]})");
        }
    }

    /// <summary>
    /// A headed session ends when the person closes the window; there is nothing else to wait
    /// for, and no timeout that would not be either rude or useless.
    /// </summary>
    private static async Task WaitForClose(IPage page)
    {
        var closed = new TaskCompletionSource();
        page.Close += (_, _) => closed.TrySetResult();

        await closed.Task.ConfigureAwait(false);
    }

    private static async Task<IPlaywright> CreatePlaywright()
    {
        try
        {
            return await Playwright.CreateAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            throw new AutobahnException(
                "Playwright could not start. Install its browsers once with 'playwright install chromium', "
                + $"or set PLAYWRIGHT_BROWSERS_PATH to an existing install. ({ex.Message})");
        }
    }

    private static ScenarioCodeOptions CodeOptions(string url, CliOptions options)
    {
        var origin = new Uri(url).GetLeftPart(UriPartial.Authority);

        return new ScenarioCodeOptions
        {
            ScenarioName = options.TestName ?? ScenarioNameFor(url),
            Namespace = options.RecordNamespace,
            BaseAddress = origin,
            StepPerRequest = true
        };
    }

    private static string DefaultFileName(string url, CliOptions options) =>
        options.RecordNamespace is null ? $"{ScenarioNameFor(url)}.csx" : $"{ScenarioNameFor(url)}.cs";

    private static string ScenarioNameFor(string url)
    {
        var host = new Uri(url).Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        var cleaned = new string(host.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');

        return cleaned.Length == 0 ? "recorded" : cleaned;
    }

    /// <summary>
    /// Collects the requests a page makes, filtering as it goes.
    /// </summary>
    /// <remarks>
    /// Filtered while recording rather than afterwards, because a page load is mostly static
    /// assets and analytics beacons; keeping them and dropping them later would mean holding
    /// hundreds of requests to emit a dozen.
    /// </remarks>
    private sealed class RequestRecorder(string startUrl, CliOptions options)
    {
        private readonly List<HttpRequest> _requests = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Lock _sync = new();
        private readonly string _origin = new Uri(startUrl).GetLeftPart(UriPartial.Authority);

        public IReadOnlyList<HttpRequest> Requests
        {
            get { lock (_sync) return [.. _requests]; }
        }

        public void Observe(IRequest request)
        {
            if (!Keep(request)) return;

            lock (_sync)
            {
                // The same call fired twice in a session is the same step, and emitting it
                // twice would put two identical blocks in the generated file.
                if (!_seen.Add($"{request.Method} {request.Url}")) return;

                _requests.Add(Convert(request, options.KeepBrowserHeaders));
            }
        }

        private bool Keep(IRequest request)
        {
            // Only the site under test. A page pulls in fonts, analytics and embeds from
            // half the internet, and none of it is what the test is about.
            if (options.SameOriginOnly && !request.Url.StartsWith(_origin, StringComparison.OrdinalIgnoreCase))
                return false;

            if (options.IncludeAssets) return true;

            return request.ResourceType is not ("image" or "stylesheet" or "font" or "media" or "script"
                or "manifest" or "other" or "ping" or "beacon");
        }

        private static HttpRequest Convert(IRequest request, bool keepBrowserHeaders)
        {
            var converted = HttpRequest.Create(new HttpMethod(request.Method), request.Url);

            foreach (var (name, value) in request.Headers)
            {
                if (name.StartsWith(':')) continue;
                if (SkipHeaders.Contains(name)) continue;
                if (!keepBrowserHeaders && IsBrowserChrome(name)) continue;

                converted = converted.WithHeader(name, value);
            }

            if (request.PostData is { Length: > 0 } body)
            {
                var contentType = request.Headers.TryGetValue("content-type", out var declared)
                    ? declared.Split(';')[0].Trim()
                    : "application/json";

                converted = converted.WithStringBody(body, contentType);
            }

            return converted;
        }

        /// <summary>
        /// The same list the HAR converter drops, and for the same reasons: one session's
        /// credentials, and headers the client decides for itself.
        /// </summary>
        private static readonly HashSet<string> SkipHeaders =
            new(Har.HarFilter.Default.SkipHeaders, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Headers that describe the browser rather than the request.
        /// </summary>
        /// <remarks>
        /// A recorded session is full of <c>sec-ch-ua</c>, <c>sec-fetch-*</c> and a user-agent
        /// string, none of which an HTTP client should be claiming and all of which make the
        /// generated file unreadable. <c>referer</c> and <c>origin</c> are deliberately not
        /// here: an API that enforces CORS or checks its referrer needs them, and dropping one
        /// silently turns a working request into a 403.
        /// </remarks>
        private static bool IsBrowserChrome(string name) =>
            name.StartsWith("sec-", StringComparison.OrdinalIgnoreCase)
            || name.Equals("user-agent", StringComparison.OrdinalIgnoreCase)
            || name.Equals("upgrade-insecure-requests", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dnt", StringComparison.OrdinalIgnoreCase);
    }
}
