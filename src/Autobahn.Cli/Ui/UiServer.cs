using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autobahn.Ui.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Autobahn.Cli.Ui;

/// <summary>
/// Serves the live view of a running test: the app itself, the run's state, and a socket
/// carrying each reporting interval as it closes.
/// </summary>
/// <remarks>
/// Started in-process beside the run and torn down with it. Everything it serves comes from a
/// <see cref="RunFeed"/> the run writes to, so nothing a client does can reach the engine -
/// which is the point. See TODO.md section 8.
/// </remarks>
internal sealed class UiServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly RunFeed _feed;

    private UiServer(WebApplication app, RunFeed feed, string url, string token)
    {
        _app = app;
        _feed = feed;
        Url = url;
        Token = token;
    }

    /// <summary>The URL to hand a person, access token and all.</summary>
    public string Url { get; }

    /// <summary>
    /// The per-run token every request must carry.
    /// </summary>
    /// <remarks>
    /// Loopback is not a security boundary on a shared machine: any process running as any
    /// user can reach 127.0.0.1. A token in the URL is not much, but it is the difference
    /// between "anyone on this box" and "anyone this URL was given to", and stopping a run is
    /// something this surface can do.
    /// </remarks>
    public string Token { get; }

    public static async Task<UiServer> StartAsync(
        UiOptions options, RunFeed feed, CancellationToken cancellationToken)
    {
        var token = CreateToken();
        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(options.BindAddress, options.Port));
        builder.Services.AddSingleton(feed);

        var app = builder.Build();

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        MapEndpoints(app, feed, token);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var address = ResolveAddress(app, options);

        return new UiServer(app, feed, $"{address}/?token={token}", token);
    }

    private static void MapEndpoints(WebApplication app, RunFeed feed, string token)
    {
        // Every endpoint, assets included, behind the token. A page that could be loaded
        // without one would tell a passer-by the URL is real and the run is happening.
        app.Use(async (context, next) =>
        {
            if (!IsAuthorised(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("This run's UI needs its access token.").ConfigureAwait(false);
                return;
            }

            IssueCookie(context, token);

            // No external anything. The page has to render on an air-gapped build agent, and
            // a CSP that says so is how a stray CDN reference gets caught in development
            // rather than in the place with no network.
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; "
                + "img-src 'self' data:; font-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'";

            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";

            await next().ConfigureAwait(false);
        });

        app.MapGet("/api/run", () => Results.Json(feed.Run, JsonOptions));
        app.MapGet("/api/snapshot", () => Results.Json(feed.Snapshot(), JsonOptions));

        app.MapGet("/api/history", (long? from) =>
            Results.Json(feed.History(from ?? 1), JsonOptions));

        app.MapGet("/api/reports", () => Results.Json(feed.Snapshot().Reports, JsonOptions));

        // Named rather than indexed: the descriptor the UI already holds carries file names,
        // and a name checked against the reports that were actually written cannot be talked
        // into reading something else out of the folder.
        app.MapGet("/api/reports/{name}", (string name) =>
        {
            var report = feed.Snapshot().Reports.FirstOrDefault(x => x.FileName == name);
            if (report is null) return Results.NotFound();

            // Absolute: the report folder is usually relative to wherever the run started, and
            // Results.File will not take a relative path.
            var path = Path.GetFullPath(Path.Combine(feed.ReportFolder, name));

            // Served inline rather than as a download, because the button next to it says
            // "Open": a browser renders the txt, json and html reports itself and downloads
            // whatever it cannot.
            return File.Exists(path)
                ? Results.File(path, ReportContentType(name))
                : Results.NotFound();
        });

        app.MapGet("/api/runs", () =>
            Results.Json(PastRuns.List(feed.ReportFolder, feed.SessionId), JsonOptions));

        app.MapGet("/api/runs/{id}", (string id) =>
            PastRuns.Detail(feed.ReportFolder, id, feed.SessionId) is { } detail
                ? Results.Json(detail, JsonOptions)
                : Results.NotFound());

        app.MapPost("/api/control/stop", (bool? force, HttpContext context) =>
        {
            // A control action needs the confirmation header as well as the token: a token in
            // a URL survives in shell history and chat logs, and stopping someone's run
            // because they pasted a link is not a failure mode worth having.
            if (!context.Request.Headers.ContainsKey("X-Autobahn-Confirm"))
            {
                return Results.Json(
                    new ControlResult { Accepted = false, Message = "This action needs the X-Autobahn-Confirm header." },
                    JsonOptions,
                    statusCode: StatusCodes.Status428PreconditionRequired);
            }

            if (feed.OnStopRequested is not { } stop)
            {
                return Results.Json(
                    new ControlResult { Accepted = false, Message = "There is no run to stop." },
                    JsonOptions,
                    statusCode: StatusCodes.Status409Conflict);
            }

            stop(force == true);

            return Results.Json(
                new ControlResult
                {
                    Accepted = true,
                    Message = force == true ? "Stopping now." : "Stopping when the current iterations finish.",
                    State = RunState.Stopping
                },
                JsonOptions);
        });

        app.Map("/api/live", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await Stream(socket, feed, context.RequestAborted).ConfigureAwait(false);
        });

        UiAssets.Map(app);
    }

    /// <summary>
    /// Writes frames to one client until it goes away.
    /// </summary>
    /// <remarks>
    /// A write that fails ends this client's stream and nothing else. The run is not told, and
    /// would not care: a browser closing is not an event a load test has an opinion about.
    /// </remarks>
    private static async Task Stream(WebSocket socket, RunFeed feed, CancellationToken cancellationToken)
    {
        using var subscription = feed.Subscribe();

        try
        {
            await foreach (var frame in subscription.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);

                await socket
                    .SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away, or the run ended. Both are the normal way this stops.
        }
        catch (WebSocketException)
        {
            // A connection that broke mid-write. Same.
        }
    }

    private static string ReportContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".csv" => "text/csv; charset=utf-8",
        ".md" => "text/markdown; charset=utf-8",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };

    private static bool IsAuthorised(HttpContext context, string token)
    {
        if (context.Request.Query.TryGetValue("token", out var fromQuery) && Matches(fromQuery.ToString(), token))
            return true;

        if (context.Request.Headers.TryGetValue("X-Autobahn-Token", out var fromHeader)
            && Matches(fromHeader.ToString(), token))
            return true;

        return context.Request.Cookies.TryGetValue(CookieName, out var fromCookie)
               && Matches(fromCookie, token);
    }

    private const string CookieName = "autobahn-ui-token";

    /// <summary>
    /// Hands the token back as a cookie, so the page's own scripts can be fetched.
    /// </summary>
    /// <remarks>
    /// A browser does not carry a query string onto the sub-resources a page asks for, so the
    /// token in the printed URL authorises the document and nothing in it - the page would load
    /// and every script under it would 401. The alternative is rewriting the token into every
    /// src attribute, which puts it in the DOM and in every network log line.
    ///
    /// Session-scoped, host-only and same-site: it lives as long as the browser is open, is
    /// never sent anywhere but this loopback server, and is unreadable from script.
    /// </remarks>
    private static void IssueCookie(HttpContext context, string token)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var existing) && Matches(existing, token)) return;

        context.Response.Cookies.Append(
            CookieName,
            token,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                Path = "/"
            });
    }

    /// <summary>Constant-time, so a wrong token cannot be found one character at a time.</summary>
    private static bool Matches(string candidate, string token) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(token));

    private static string CreateToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// The URL a person can actually click, which is not always the one Kestrel reports.
    /// </summary>
    /// <remarks>
    /// Port 0 means Kestrel picked one, and binding to anything other than a single address
    /// leaves it reporting a wildcard - neither of which a browser can open. Both are resolved
    /// back to something concrete here.
    /// </remarks>
    private static string ResolveAddress(WebApplication app, UiOptions options)
    {
        var reported = app.Urls.FirstOrDefault() ?? $"http://{options.BindAddress}:{options.Port}";

        if (!reported.Contains("[::]") && !reported.Contains("0.0.0.0")) return reported.TrimEnd('/');

        var port = new Uri(reported.Replace("[::]", "0.0.0.0")).Port;
        return $"http://{IPAddress.Loopback}:{port}";
    }

    /// <summary>
    /// One serializer setting for the whole surface.
    /// </summary>
    /// <remarks>
    /// camelCase property names and string enums, because the other end of this wire is
    /// JavaScript and <c>state: "Bombing"</c> reads better in a browser's network tab than
    /// <c>state: 2</c>.
    ///
    /// The enum *values* keep their declared casing while the property names do not, which
    /// looks inconsistent and is deliberate: the client parses them back into the same enum by
    /// name, and that parse is case-sensitive. A camelCased <c>"bombing"</c> would deserialize
    /// to <c>Init</c> on the other end without anything failing.
    /// </remarks>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
            // A server that will not stop politely is disposed anyway; a load test's teardown
            // is not the place to fail over a goodbye.
        }

        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
