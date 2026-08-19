using System.Net;

namespace Autobahn.Http;

/// <summary>How the clients a load test uses are built and shared.</summary>
public sealed record HttpClientSettings
{
    /// <summary>
    /// How many connections one client opens to one server before requests start queueing.
    /// </summary>
    /// <remarks>
    /// The .NET default is unlimited for HTTP/1.1 through <c>SocketsHttpHandler</c>, which is
    /// usually what a load test wants - a cap here silently becomes the thing being measured.
    /// It is settable because sometimes modelling a client with a bounded pool is the point.
    /// </remarks>
    public int? MaxConnectionsPerServer { get; init; }

    /// <summary>
    /// How long a pooled connection is reused before being replaced.
    /// </summary>
    /// <remarks>
    /// Two minutes rather than forever: a connection held for the whole run never sees a DNS
    /// change, so a test against a load-balanced host would keep hammering whichever node it
    /// first resolved and report that node's numbers as the service's.
    /// </remarks>
    public TimeSpan PooledConnectionLifetime { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan? PooledConnectionIdleTimeout { get; init; }

    /// <summary>The client's own timeout, applied when a request does not set its own.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>Whether the client follows 3xx responses. Off by default, so a redirect is measurable.</summary>
    public bool AllowAutoRedirect { get; init; }

    /// <summary>Whether the handler transparently decompresses responses.</summary>
    public DecompressionMethods AutomaticDecompression { get; init; } = DecompressionMethods.All;

    /// <summary>
    /// Gives each client its own cookie jar, so one virtual user's session does not leak into
    /// another's. Off by default: a cookie container is a lock on the hot path, and most tests
    /// do not need one.
    /// </summary>
    public bool UseCookies { get; init; }

    /// <summary>Prepended to a request's URL when the request gives a relative one.</summary>
    public string? BaseAddress { get; init; }

    public static HttpClientSettings Default { get; } = new();
}

/// <summary>
/// Builds the HTTP clients a scenario uses, one per virtual user or one shared.
/// </summary>
/// <remarks>
/// Which of those two a test wants is a real decision, not a detail. One shared client is
/// right when the target is a service and connection reuse is realistic; one client per copy
/// is right when each virtual user is a distinct session - a distinct cookie jar, a distinct
/// set of connections - and sharing would make them one user with N times the traffic.
/// </remarks>
public static class HttpClientPool
{
    /// <summary>One client, built from the given settings.</summary>
    public static HttpClient CreateClient(HttpClientSettings? settings = null)
    {
        var config = settings ?? HttpClientSettings.Default;

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = config.PooledConnectionLifetime,
            AllowAutoRedirect = config.AllowAutoRedirect,
            AutomaticDecompression = config.AutomaticDecompression,
            UseCookies = config.UseCookies
        };

        if (config.MaxConnectionsPerServer is { } max) handler.MaxConnectionsPerServer = max;
        if (config.PooledConnectionIdleTimeout is { } idle) handler.PooledConnectionIdleTimeout = idle;
        if (config.UseCookies) handler.CookieContainer = new CookieContainer();

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = config.Timeout };

        if (config.BaseAddress is { } baseAddress) client.BaseAddress = new Uri(baseAddress);

        return client;
    }

    /// <summary>
    /// A pool of <paramref name="count"/> clients, handed out by copy index - so copy 7 always
    /// gets the same client, and with <see cref="HttpClientSettings.UseCookies"/> the same
    /// session too.
    /// </summary>
    public static ClientPool<HttpClient> CreatePool(int count, HttpClientSettings? settings = null)
    {
        if (count < 1)
            throw new AutobahnException($"An HTTP client pool of {count} clients is not something a scenario can use.");

        var pool = new ClientPool<HttpClient>();

        for (var i = 0; i < count; i++) pool.AddClient(CreateClient(settings));

        return pool;
    }
}
