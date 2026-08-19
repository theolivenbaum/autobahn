using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Autobahn.Http;

/// <summary>
/// A request to measure, plus everything Autobahn needs to know to judge the answer.
/// </summary>
/// <remarks>
/// Not an <see cref="HttpRequestMessage"/>, and deliberately: one of those can only be sent
/// once, so a scenario reusing a request across iterations would fail on the second. This is
/// a description that builds a fresh message each time, and carries the checks, the timeout
/// and the tracing flag that a raw message has nowhere to put.
/// </remarks>
public sealed record HttpRequest
{
    private HttpRequest(HttpMethod method, string url)
    {
        Method = method;
        Url = url;
    }

    // The factories live here rather than on a class called Http, because a class with the
    // same name as its own namespace binds to the namespace inside anything under a shared
    // root - so `Http.Get` would fail to compile in half the places it is written.

    public static HttpRequest Get(string url) => new(HttpMethod.Get, url);
    public static HttpRequest Post(string url) => new(HttpMethod.Post, url);
    public static HttpRequest Put(string url) => new(HttpMethod.Put, url);
    public static HttpRequest Patch(string url) => new(HttpMethod.Patch, url);
    public static HttpRequest Delete(string url) => new(HttpMethod.Delete, url);
    public static HttpRequest Head(string url) => new(HttpMethod.Head, url);
    public static HttpRequest Options(string url) => new(HttpMethod.Options, url);

    public static HttpRequest Create(HttpMethod method, string url) => new(method, url);

    public HttpMethod Method { get; }
    public string Url { get; }

    internal IReadOnlyList<(string Name, string Value)> Headers { get; init; } = [];
    internal Func<HttpContent>? CreateContent { get; init; }
    internal TimeSpan? Timeout { get; init; }
    internal IReadOnlyList<HttpCheck> Checks { get; init; } = [];
    internal bool Trace { get; init; }

    /// <summary>Adds a header. Repeating a name adds a second value rather than replacing the first.</summary>
    public HttpRequest WithHeader(string name, string value) =>
        this with { Headers = [.. Headers, (name, value)] };

    public HttpRequest WithHeaders(IEnumerable<KeyValuePair<string, string>> headers) =>
        this with { Headers = [.. Headers, .. headers.Select(x => (x.Key, x.Value))] };

    public HttpRequest WithBearerToken(string token) => WithHeader("Authorization", $"Bearer {token}");

    /// <summary>Sends the value as JSON, with the content type to match.</summary>
    public HttpRequest WithJsonBody<T>(T body, JsonSerializerOptions? options = null) =>
        this with
        {
            CreateContent = () => new StringContent(
                JsonSerializer.Serialize(body, options), Encoding.UTF8, "application/json")
        };

    public HttpRequest WithStringBody(string body, string contentType = "text/plain") =>
        this with { CreateContent = () => new StringContent(body, Encoding.UTF8, contentType) };

    public HttpRequest WithBytesBody(byte[] body, string contentType = "application/octet-stream") =>
        this with
        {
            CreateContent = () =>
            {
                var content = new ByteArrayContent(body);
                content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                return content;
            }
        };

    public HttpRequest WithFormBody(IEnumerable<KeyValuePair<string, string>> fields)
    {
        var copy = fields.ToArray();
        return this with { CreateContent = () => new FormUrlEncodedContent(copy) };
    }

    /// <summary>
    /// Builds the body yourself. The factory runs once per send, because an
    /// <see cref="HttpContent"/> cannot be sent twice.
    /// </summary>
    public HttpRequest WithContent(Func<HttpContent> createContent) =>
        this with { CreateContent = createContent };

    /// <summary>
    /// How long this request gets before it is given up on and recorded as a timeout.
    /// Overrides the client's own timeout for this request only.
    /// </summary>
    public HttpRequest WithTimeout(TimeSpan timeout) => this with { Timeout = timeout };

    /// <summary>
    /// Adds a rule the answer has to satisfy to count as a success.
    /// </summary>
    /// <remarks>
    /// Without any checks, a 2xx is a success and anything else is a failure. With checks, a
    /// 2xx that fails one of them is a failure too - which is the point: an API that answers
    /// 200 with <c>{"error": …}</c> is not succeeding, and a load test that says it is has
    /// measured the wrong thing.
    /// </remarks>
    public HttpRequest WithCheck(HttpCheck check) => this with { Checks = [.. Checks, check] };

    /// <summary>The answer must carry this status code.</summary>
    public HttpRequest WithStatusCheck(int statusCode) =>
        WithCheck(HttpCheck.Create(
            $"status is {statusCode}",
            response => (int)response.StatusCode == statusCode));

    /// <summary>The answer's body must contain this text.</summary>
    public HttpRequest WithBodyCheck(string expected) =>
        WithCheck(HttpCheck.Create($"body contains '{expected}'", (_, body) => body.Contains(expected, StringComparison.Ordinal)));

    /// <summary>
    /// Logs the request and the answer through the scenario's logger, for working out why a
    /// test is failing. Off by default and not something to leave on under load.
    /// </summary>
    public HttpRequest WithTracing(bool enabled = true) => this with { Trace = enabled };
}
