using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Autobahn.Http;

/// <summary>Sending a measured request.</summary>
public static class HttpClientExtensions
{
    /// <summary>
    /// Sends the request and turns the answer into a response Autobahn can record: the status
    /// code, the bytes that went over the wire, and whether every check passed.
    /// </summary>
    /// <remarks>
    /// The iteration's own cancellation token is passed through, so an iteration that outran
    /// its timeout actually stops the request rather than leaving it running while the actor
    /// moves on.
    /// </remarks>
    public static async Task<Response<HttpResponseMessage>> Send(
        this HttpClient client, HttpRequest request, IScenarioContext context)
    {
        using var message = Build(request, client);

        using var timeoutCts = request.Timeout is { } timeout
            ? CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken)
            : null;

        timeoutCts?.CancelAfter(request.Timeout!.Value);

        var token = timeoutCts?.Token ?? context.CancellationToken;
        var needsBody = request.Checks.Any(x => x.NeedsBody) || request.Trace;
        var requestBytes = HttpSize.OfRequest(message);

        if (request.Trace) Trace(context, request, message);

        var started = Stopwatch.GetTimestamp();

        try
        {
            // ResponseHeadersRead, so the time recorded is time to first byte plus however
            // long reading the body actually takes - rather than the handler having buffered
            // the whole body before the call even returned.
            using var response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            var body = needsBody
                ? await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)
                : null;

            var sizeBytes = requestBytes + await HttpSize.OfResponse(response, body, token).ConfigureAwait(false);
            var statusCode = ((int)response.StatusCode).ToString();
            var latencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            if (request.Trace) Trace(context, response, body);

            var failedCheck = FirstFailedCheck(request, response, body);

            if (failedCheck is not null)
            {
                return Response.Fail(
                    payload: response,
                    statusCode: statusCode,
                    sizeBytes: sizeBytes,
                    message: $"check failed: {failedCheck.Description}",
                    latencyMs: latencyMs);
            }

            // With no checks of its own, a request is judged the way HTTP judges itself.
            if (request.Checks.Count == 0 && !response.IsSuccessStatusCode)
            {
                return Response.Fail(
                    payload: response,
                    statusCode: statusCode,
                    sizeBytes: sizeBytes,
                    message: response.ReasonPhrase ?? "",
                    latencyMs: latencyMs);
            }

            return Response.Ok(payload: response, statusCode: statusCode, sizeBytes: sizeBytes, latencyMs: latencyMs);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true
                                                 && !context.CancellationToken.IsCancellationRequested)
        {
            // The request's own timeout, not the iteration's: a distinct outcome, and the one
            // that says "the target was slow" rather than "we stopped asking".
            return Response.FailOf<HttpResponseMessage>() with
            {
                StatusCode = HttpStatusCodes.RequestTimeout,
                Message = $"request timeout after {request.Timeout}",
                SizeBytes = requestBytes,
                LatencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            };
        }
        catch (HttpRequestException ex)
        {
            return Response.FailOf<HttpResponseMessage>() with
            {
                StatusCode = HttpStatusCodes.TransportError,
                Message = ex.Message,
                SizeBytes = requestBytes,
                LatencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            };
        }
    }

    private static HttpCheck? FirstFailedCheck(HttpRequest request, HttpResponseMessage response, string? body)
    {
        foreach (var check in request.Checks)
        {
            if (!check.Predicate(response, body ?? string.Empty)) return check;
        }

        return null;
    }

    private static HttpRequestMessage Build(HttpRequest request, HttpClient client)
    {
        var uri = client.BaseAddress is null || Uri.IsWellFormedUriString(request.Url, UriKind.Absolute)
            ? new Uri(request.Url, UriKind.RelativeOrAbsolute)
            : new Uri(client.BaseAddress, request.Url);

        var message = new HttpRequestMessage(request.Method, uri);

        if (request.CreateContent is { } createContent) message.Content = createContent();

        foreach (var (name, value) in request.Headers)
        {
            // A content header set on the request headers is rejected outright, so it is
            // routed to the content it actually belongs to.
            if (!message.Headers.TryAddWithoutValidation(name, value))
                message.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        return message;
    }

    private static void Trace(IScenarioContext context, HttpRequest request, HttpRequestMessage message)
    {
        var headers = string.Join("; ", message.Headers.Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));
        context.Logger.LogInformation("--> {Method} {Url} {Headers}", request.Method, message.RequestUri, headers);
    }

    private static void Trace(IScenarioContext context, HttpResponseMessage response, string? body)
    {
        context.Logger.LogInformation(
            "<-- {Status} {Reason} {Body}",
            (int)response.StatusCode,
            response.ReasonPhrase,
            body is null ? "" : Truncate(body));
    }

    /// <summary>A traced body is for reading, and a megabyte of JSON in a log line is not.</summary>
    private static string Truncate(string body) =>
        body.Length <= 2_048 ? body : $"{body[..2_048]}… ({body.Length} chars)";
}

/// <summary>The status codes Autobahn reports when HTTP itself never answered.</summary>
public static class HttpStatusCodes
{
    /// <summary>The request outran its own timeout.</summary>
    public const string RequestTimeout = "-200";

    /// <summary>The request never reached the server: DNS, connect, TLS, reset.</summary>
    public const string TransportError = "-201";
}
