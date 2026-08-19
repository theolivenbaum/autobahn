using System.Text;

namespace Autobahn.Http;

/// <summary>
/// How many bytes a request and its answer actually put on the wire.
/// </summary>
/// <remarks>
/// Counting only the response body - which is the easy thing to do, and what most load tests
/// report - understates a small-payload API badly: a 40-byte JSON answer with 400 bytes of
/// headers is ten times the traffic the body suggests. So the request line, every header and
/// both bodies are counted. It is still an approximation, and honestly so: it is the HTTP/1.1
/// wire form of the message, before TLS and before HTTP/2 header compression, because those
/// happen below where any of this is visible.
/// </remarks>
internal static class HttpSize
{
    private const int CrLf = 2;

    public static long OfRequest(HttpRequestMessage message)
    {
        // "METHOD path HTTP/1.1\r\n"
        var total = (long)message.Method.Method.Length
                    + 1 + (message.RequestUri?.PathAndQuery.Length ?? 1)
                    + 1 + "HTTP/1.1".Length + CrLf;

        total += HeaderBytes(message.Headers);

        if (message.Content is not null)
        {
            total += HeaderBytes(message.Content.Headers);
            total += message.Content.Headers.ContentLength ?? 0;
        }

        return total + CrLf;
    }

    public static async Task<long> OfResponse(
        HttpResponseMessage response, string? body, CancellationToken cancellationToken)
    {
        // "HTTP/1.1 200 Reason\r\n"
        var total = (long)"HTTP/1.1".Length
                    + 1 + 3
                    + 1 + (response.ReasonPhrase?.Length ?? 0) + CrLf;

        total += HeaderBytes(response.Headers);
        total += HeaderBytes(response.Content.Headers);
        total += CrLf;

        // Content-Length when the server sent one; otherwise what was actually read. A
        // chunked response with nothing reading it has no length to report, and guessing one
        // would be worse than saying zero.
        if (response.Content.Headers.ContentLength is { } declared) return total + declared;

        if (body is not null) return total + Encoding.UTF8.GetByteCount(body);

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return total + bytes.Length;
    }

    private static long HeaderBytes(System.Net.Http.Headers.HttpHeaders headers)
    {
        var total = 0L;

        foreach (var (name, values) in headers)
        {
            foreach (var value in values)
            {
                // "Name: value\r\n"
                total += name.Length + 2 + value.Length + CrLf;
            }
        }

        return total;
    }
}
