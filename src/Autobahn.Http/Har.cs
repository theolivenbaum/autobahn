using System.Text.Json;
using System.Text.Json.Serialization;

namespace Autobahn.Http;

/// <summary>
/// Turns a recorded browser session into the beginning of a load test.
/// </summary>
/// <remarks>
/// A realistic test should not start from a blank file. Every browser can export a HAR - the
/// network tab, "save all as HAR" - and that recording already contains the requests, their
/// order, their headers and their bodies. This reads one and hands back
/// <see cref="HttpRequest"/> values ready to send.
///
/// What comes out is a *starting point*, and honestly so. A recording has one session's
/// cookies, one user's ids and one moment's tokens baked into it; those have to be replaced
/// with a feed before the test means anything. What the conversion saves is the transcription,
/// which is the tedious part.
/// </remarks>
public static class Har
{
    /// <summary>What to leave out of the converted requests.</summary>
    public sealed record HarFilter
    {
        /// <summary>
        /// Skip requests for images, stylesheets, fonts and scripts.
        /// </summary>
        /// <remarks>
        /// On by default. A page load is mostly static assets, and a load test that replays
        /// them measures the CDN rather than the application - while burying the handful of
        /// requests that were the point.
        /// </remarks>
        public bool SkipStaticAssets { get; init; } = true;

        /// <summary>Only keep requests whose URL contains one of these. Empty keeps everything.</summary>
        public IReadOnlyList<string> UrlContains { get; init; } = [];

        /// <summary>Drop requests whose URL contains one of these.</summary>
        public IReadOnlyList<string> UrlExcludes { get; init; } = [];

        /// <summary>
        /// Headers not carried over.
        /// </summary>
        /// <remarks>
        /// The defaults are the ones that are either the recording's own session - cookies and
        /// authorization, which must come from a feed instead - or headers the client sets for
        /// itself and would be wrong to replay: the HTTP/2 pseudo-headers, the hop-by-hop ones,
        /// and content length, which the content decides.
        /// </remarks>
        public IReadOnlyList<string> SkipHeaders { get; init; } =
        [
            "cookie", "authorization", "host", "content-length", "connection",
            "keep-alive", "transfer-encoding", "upgrade", "accept-encoding"
        ];

        /// <summary>Keep only successful recorded responses. On by default.</summary>
        public bool OnlySuccessful { get; init; } = true;

        public static HarFilter Default { get; } = new();
    }

    /// <summary>Reads a HAR file and converts each entry that survives the filter.</summary>
    public static IReadOnlyList<HttpRequest> FromFile(string filePath, HarFilter? filter = null)
    {
        if (!File.Exists(filePath))
            throw new AutobahnException($"HAR file not found: '{filePath}'.");

        return Parse(File.ReadAllText(filePath), filter);
    }

    /// <summary>Converts a HAR document already in memory.</summary>
    public static IReadOnlyList<HttpRequest> Parse(string harJson, HarFilter? filter = null)
    {
        var rules = filter ?? HarFilter.Default;

        HarDocument? document;

        try
        {
            document = JsonSerializer.Deserialize<HarDocument>(harJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new AutobahnException($"That does not read as a HAR document: {ex.Message}");
        }

        var entries = document?.Log?.Entries;

        if (entries is null || entries.Count == 0)
            throw new AutobahnException("The HAR document has no entries in it.");

        var requests = new List<HttpRequest>();

        foreach (var entry in entries)
        {
            if (entry.Request is not { } recorded) continue;
            if (!Keep(entry, recorded, rules)) continue;

            requests.Add(Convert(recorded, rules));
        }

        return requests;
    }

    private static bool Keep(HarEntry entry, HarRequest request, HarFilter rules)
    {
        if (string.IsNullOrWhiteSpace(request.Url)) return false;

        if (rules.OnlySuccessful && entry.Response is { Status: var status } && status is < 200 or >= 400)
            return false;

        if (rules.SkipStaticAssets && LooksStatic(entry, request.Url)) return false;

        if (rules.UrlExcludes.Any(x => request.Url.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return false;

        return rules.UrlContains.Count == 0
               || rules.UrlContains.Any(x => request.Url.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Static by the recorded content type where there is one, and by extension otherwise -
    /// because a HAR from a proxy often has no MIME type recorded at all.
    /// </summary>
    private static bool LooksStatic(HarEntry entry, string url)
    {
        var mime = entry.Response?.Content?.MimeType;

        if (!string.IsNullOrEmpty(mime))
        {
            if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return true;
            if (mime.StartsWith("font/", StringComparison.OrdinalIgnoreCase)) return true;
            if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return true;
            if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return true;
            if (mime.Contains("javascript", StringComparison.OrdinalIgnoreCase)) return true;
            if (mime.Contains("css", StringComparison.OrdinalIgnoreCase)) return true;
        }

        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var extension = Path.GetExtension(path);

        return extension.ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".ico" or ".avif"
            or ".css" or ".js" or ".mjs" or ".map"
            or ".woff" or ".woff2" or ".ttf" or ".otf" or ".eot";
    }

    private static HttpRequest Convert(HarRequest recorded, HarFilter rules)
    {
        var request = HttpRequest.Create(new HttpMethod(recorded.Method ?? "GET"), recorded.Url!);

        foreach (var header in recorded.Headers ?? [])
        {
            if (header.Name is null || header.Value is null) continue;

            // HTTP/2 pseudo-headers describe the frame, not the request.
            if (header.Name.StartsWith(':')) continue;

            if (rules.SkipHeaders.Contains(header.Name, StringComparer.OrdinalIgnoreCase)) continue;

            request = request.WithHeader(header.Name, header.Value);
        }

        if (recorded.PostData is { Text: { Length: > 0 } text })
        {
            var contentType = recorded.PostData.MimeType is { Length: > 0 } mime
                ? mime.Split(';')[0].Trim()
                : "application/json";

            request = request.WithStringBody(text, contentType);
        }

        return request;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    // Only the parts of the HAR schema a conversion needs. A HAR carries timings, cache
    // entries and page groupings too; none of them says anything about what to send.

    private sealed record HarDocument
    {
        public HarLog? Log { get; init; }
    }

    private sealed record HarLog
    {
        public List<HarEntry>? Entries { get; init; }
    }

    private sealed record HarEntry
    {
        public HarRequest? Request { get; init; }
        public HarResponse? Response { get; init; }
    }

    private sealed record HarRequest
    {
        public string? Method { get; init; }
        public string? Url { get; init; }
        public List<HarHeader>? Headers { get; init; }
        public HarPostData? PostData { get; init; }
    }

    private sealed record HarResponse
    {
        public int Status { get; init; }
        public HarContent? Content { get; init; }
    }

    private sealed record HarContent
    {
        public string? MimeType { get; init; }
    }

    private sealed record HarHeader
    {
        public string? Name { get; init; }
        public string? Value { get; init; }
    }

    private sealed record HarPostData
    {
        public string? MimeType { get; init; }
        public string? Text { get; init; }
    }
}
