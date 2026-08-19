using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Transpose;
using Autobahn.Ui.Contracts;
using static Transpose.Core.dom;

namespace Autobahn.Ui
{
    /// <summary>
    /// Talks to the host: fetches the run, then follows it over a WebSocket.
    /// </summary>
    /// <remarks>
    /// Everything the views bind to lives in <see cref="DashboardState"/> as an observable, so
    /// a frame arriving appends a point and the components that care re-render themselves.
    /// Nothing polls.
    ///
    /// The socket is expected to drop - a laptop sleeps, a proxy times out, a run ends - so
    /// reconnecting is the normal path rather than the error path: back off, reconnect, and
    /// backfill whatever was missed by sequence number. A chart with an invisible hole in it
    /// is worse than one that knows to go and ask.
    ///
    /// The reads are an ordinary <c>HttpClient</c>, which Transpose implements over the
    /// browser's fetch, and the socket is the browser's own <c>WebSocket</c> as Transpose
    /// exposes it. Both are typed bindings on purpose: the handlers were written through
    /// <c>Script.Write</c> first, and a delegate interpolated into an anonymous function is
    /// re-bound to whatever <c>this</c> is at the call site - which, inside <c>onopen</c>, is
    /// the socket rather than this object.
    /// </remarks>
    public sealed class RunClient
    {
        /// <summary>
        /// The host serializes property names in camelCase, so the reader has to expect them.
        /// </summary>
        /// <remarks>
        /// Enum values are *not* camelCased on the wire, deliberately: they come back through
        /// <c>Enum.parse</c>, which matches by name and is case-sensitive, so a lowercased
        /// <c>"bombing"</c> would quietly deserialize to the first member instead of failing.
        ///
        /// <c>Replace</c> is not a preference either. The records default their collections to
        /// empty arrays, and the default handling *reuses* whatever a property already holds -
        /// so every array in every document would arrive as the empty one it was initialized
        /// with, and a dashboard would render as though the run had no scenarios.
        /// </remarks>
        private static readonly JsonSerializerSettings Wire = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            ObjectCreationHandling = ObjectCreationHandling.Replace
        };

        // Absolute or nothing: this HttpClient is the browser's fetch behind a familiar API,
        // and a relative path throws rather than resolving against the page it is running in.
        private readonly HttpClient _http = new HttpClient { BaseAddress = new Uri(window.location.origin) };
        private readonly string _token;
        private readonly DashboardState _state;

        private WebSocket _socket;
        private int _retryDelayMs = 500;
        private bool _closed;

        public RunClient(DashboardState state)
        {
            _state = state;
            _token = TokenFromUrl();
        }

        /// <summary>
        /// Whether this page is a static export rather than a live view.
        /// </summary>
        /// <remarks>
        /// An exported page is the same application reading a snapshot the exporter wrote into
        /// the document, so there is nothing to connect to and nothing to stop - which the
        /// shell has to know, or it would offer a stop button that could not work.
        /// </remarks>
        public bool IsStatic => Script.Write<bool>("(typeof window.__autobahnSnapshot !== 'undefined')");

        public void Start()
        {
            if (IsStatic)
            {
                Embedded();
                return;
            }

            LoadSnapshot();
            Connect();
        }

        /// <summary>Applies the snapshot the exporter wrote into the page.</summary>
        private void Embedded()
        {
            var json = Script.Write<string>("JSON.stringify(window.__autobahnSnapshot)");
            var snapshot = JsonConvert.DeserializeObject<RunSnapshot>(json, Wire);

            if (snapshot != null) _state.Apply(snapshot);
        }

        /// <summary>
        /// The token the CLI put in the URL it printed.
        /// </summary>
        /// <remarks>
        /// Read from the address bar rather than stored: this page is only ever opened from a
        /// link the tool printed, and a token in local storage would outlive the run it
        /// belongs to.
        /// </remarks>
        private static string TokenFromUrl()
        {
            var search = window.location.search;
            if (string.IsNullOrEmpty(search)) return "";

            var pairs = search.TrimStart('?').Split('&');

            for (var i = 0; i < pairs.Length; i++)
            {
                var parts = pairs[i].Split('=');
                if (parts.Length == 2 && parts[0] == "token") return parts[1];
            }

            return "";
        }

        private string WithToken(string path) =>
            path + (path.IndexOf('?') >= 0 ? "&" : "?") + "token=" + _token;

        private async void LoadSnapshot()
        {
            var snapshot = await Get<RunSnapshot>("/api/snapshot");
            if (snapshot != null) _state.Apply(snapshot);
        }

        private void Connect()
        {
            if (_closed) return;

            var scheme = window.location.protocol == "https:" ? "wss://" : "ws://";
            var url = scheme + window.location.host + WithToken("/api/live");

            _socket = new WebSocket(url);

            _socket.onopen = _ => OnOpen();
            _socket.onmessage = e => OnMessage(e.data.ToString());
            _socket.onclose = _ => OnClose();
            _socket.onerror = _ => OnError();
        }

        /// <summary>
        /// Only reset the backoff once a connection actually opened. Resetting on the attempt
        /// would turn a server that accepts and immediately drops into a tight reconnect loop.
        /// </summary>
        private void OnOpen()
        {
            _retryDelayMs = 500;
            _state.Connected.Value = true;
        }

        private void OnMessage(string json)
        {
            var frame = JsonConvert.DeserializeObject<LiveFrame>(json, Wire);
            if (frame == null) return;

            var expected = _state.LastSequence + 1;

            // A gap: frames went past while this client was away. Fill it before appending,
            // so the charts stay continuous rather than jumping.
            if (_state.LastSequence > 0 && frame.Sequence > expected) Backfill(expected, frame);
            else _state.Append(frame);

            // The reports are written as the run ends, so a page that was already open when it
            // did would otherwise keep the empty list it took at load time forever.
            if (frame.State == RunState.Finished || frame.State == RunState.Failed) LoadReports();
        }

        private async void LoadReports()
        {
            var reports = await Get<ReportDescriptor[]>("/api/reports");
            if (reports != null) _state.Reports.Value = reports;
        }

        private void OnClose()
        {
            _state.Connected.Value = false;
            Reconnect();
        }

        private void OnError()
        {
            _state.Connected.Value = false;
        }

        private async void Backfill(double from, LiveFrame pending)
        {
            var history = await Get<HistoryResponse>("/api/history?from=" + from);

            if (history == null)
            {
                _state.Append(pending);
                return;
            }

            // Asked for further back than the host still has. Stitching from here would
            // silently invent continuity, so the whole state is taken again instead.
            if (history.OldestSequence > from)
            {
                LoadSnapshot();
                return;
            }

            for (var i = 0; i < history.Frames.Length; i++) _state.Append(history.Frames[i]);

            _state.Append(pending);
        }

        private void Reconnect()
        {
            if (_closed) return;

            var delay = _retryDelayMs;
            _retryDelayMs = _retryDelayMs * 2 > 10000 ? 10000 : _retryDelayMs * 2;

            window.setTimeout(_ => Connect(), delay);
        }

        /// <summary>
        /// One report's own URL, token and all - every endpoint here needs one.
        /// </summary>
        /// <remarks>
        /// Absolute, because it is also what a link hands the browser, and because this page
        /// may be sitting on a client-side route rather than at the root.
        /// </remarks>
        public string ReportUrl(string fileName) =>
            window.location.origin + WithToken("/api/reports/" + fileName);

        /// <summary>One report as text, for the previews. Null when it could not be read.</summary>
        public Task<string> LoadReport(string fileName) => GetText("/api/reports/" + fileName);

        /// <summary>The runs found beside this one in the report folder, newest first.</summary>
        public async Task<PastRunSummary[]> LoadRuns() =>
            await Get<PastRunSummary[]>("/api/runs") ?? new PastRunSummary[0];

        /// <summary>One past run in the detail a comparison needs, or null if it is gone.</summary>
        public Task<PastRunDetail> LoadRun(string id) => Get<PastRunDetail>("/api/runs/" + id);

        /// <summary>
        /// Asks the host to stop the run.
        /// </summary>
        /// <remarks>
        /// The confirmation header is not optional and is not a formality: a token in a URL
        /// survives in shell history and chat logs, and stopping someone's run because they
        /// pasted a link is not a failure mode worth having.
        /// </remarks>
        public async Task<ControlResult> RequestStop(bool force)
        {
            var url = WithToken("/api/control/stop") + (force ? "&force=true" : "");
            var request = new HttpRequestMessage(HttpMethod.Post, url);

            request.Headers.Add("X-Autobahn-Confirm", "yes");

            try
            {
                var response = await _http.SendAsync(request);
                var body = response.Content.ReadAsString();

                return JsonConvert.DeserializeObject<ControlResult>(body, Wire)
                       ?? new ControlResult { Accepted = false, Message = "The host said nothing." };
            }
            catch (Exception ex)
            {
                return new ControlResult { Accepted = false, Message = ex.Message };
            }
        }

        public void Close()
        {
            _closed = true;
            if (_socket != null) _socket.close();
        }

        /// <summary>
        /// A GET that gives back the parsed body, or null when it did not arrive.
        /// </summary>
        /// <remarks>
        /// A failed fetch here is a host that went away, which the socket's close handler is
        /// already dealing with. Two error paths for one event would produce two banners.
        /// </remarks>
        private async Task<T> Get<T>(string path) where T : class
        {
            var body = await GetText(path);

            return body == null ? null : JsonConvert.DeserializeObject<T>(body, Wire);
        }

        private async Task<string> GetText(string path)
        {
            try
            {
                var response = await _http.GetAsync(WithToken(path));

                return response.IsSuccessStatusCode ? response.Content.ReadAsString() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
