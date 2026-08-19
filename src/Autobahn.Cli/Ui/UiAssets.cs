using System.IO.Compression;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Autobahn.Cli.Ui;

/// <summary>
/// Serves the compiled Tesserae application out of this assembly.
/// </summary>
/// <remarks>
/// Embedded rather than laid out on disk beside the tool, so the whole UI is one file that
/// travels with the dotnet tool and cannot be half-installed. Nothing is fetched from a
/// network - the CSP forbids it and the assets do not ask - because a build agent with no
/// route to the internet is exactly where somebody will want to watch a run.
///
/// The resources are stored gzipped and handed to the browser that way. An assembly holds an
/// embedded resource uncompressed, and this UI is twelve megabytes of JavaScript, CSS and icon
/// fonts uncompressed against about three compressed - which is the difference between a
/// dotnet tool people install without thinking and one they notice. A client that does not
/// advertise gzip gets it decompressed here instead.
/// </remarks>
internal static class UiAssets
{
    private const string Prefix = "Autobahn.Cli.Ui.wwwroot.";

    private static readonly Assembly Assembly = typeof(UiAssets).Assembly;

    public static void Map(WebApplication app)
    {
        app.MapGet("/favicon.ico", () =>
        {
            using var stream = Assembly.GetManifestResourceStream("Autobahn.Cli.Ui.favicon.png");
            if (stream is null) return Results.NotFound();

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            return Results.File(buffer.ToArray(), "image/png");
        });

        app.MapGet("/{**path}", (string? path, HttpContext context) =>
        {
            // Anything that is not a file is the app: the UI routes client-side, so a deep
            // link has to serve the shell rather than 404.
            var name = string.IsNullOrEmpty(path) || !Path.HasExtension(path) ? "index.html" : path;

            var content = Read(name);

            if (content is null)
            {
                return name == "index.html"
                    ? Results.Content(Placeholder(), "text/html")
                    : Results.NotFound();
            }

            if (AcceptsGzip(context))
            {
                context.Response.Headers.ContentEncoding = "gzip";
                return Results.File(content, ContentType(name));
            }

            return Results.File(Inflate(content), ContentType(name));
        });
    }

    /// <summary>Whether the compiled application was embedded in this build.</summary>
    public static bool IsBuilt => Read("index.html") is not null;

    /// <summary>One staged file, decompressed. Null when it is not there.</summary>
    public static byte[]? ReadBytes(string name) => Read(name) is { } compressed ? Inflate(compressed) : null;

    /// <summary>One staged text file, decompressed. Null when it is not there.</summary>
    public static string? ReadText(string name) =>
        ReadBytes(name) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;

    private static bool AcceptsGzip(HttpContext context) =>
        context.Request.Headers.AcceptEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase);

    /// <summary>The gzipped bytes of one staged file, or null when the UI was not built in.</summary>
    private static byte[]? Read(string name)
    {
        var resource = Prefix + name.Replace('/', '.') + ".gz";

        using var stream = Assembly.GetManifestResourceStream(resource);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return buffer.ToArray();
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var source = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var buffer = new MemoryStream();

        source.CopyTo(buffer);

        return buffer.ToArray();
    }

    private static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".woff2" => "font/woff2",
        ".map" => "application/json; charset=utf-8",
        _ => "application/octet-stream"
    };

    /// <summary>
    /// What to serve when the tool was built without the Tesserae app.
    /// </summary>
    /// <remarks>
    /// Building the UI needs the Transpose compiler as a global tool, which a clean clone does
    /// not have, so a plain <c>dotnet build</c> produces a CLI with no assets in it. That has
    /// to say so rather than serving a blank page - and the endpoints underneath it all work,
    /// which the page below points out, because that is what somebody debugging this needs.
    /// </remarks>
    private static string Placeholder() =>
        """
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Autobahn</title>
            <style>
                :root { color-scheme: light dark; }
                body { font: 15px/1.6 ui-sans-serif, system-ui, sans-serif; margin: 0; padding: 3rem 1.5rem; }
                main { max-width: 40rem; margin: 0 auto; }
                h1 { font-size: 1.4rem; margin: 0 0 1rem; }
                code { font-family: ui-monospace, monospace; font-size: 0.9em; }
                ul { padding-left: 1.2rem; }
                li { margin: 0.3rem 0; }
                p.note { opacity: 0.75; }
            </style>
        </head>
        <body>
        <main>
            <h1>Autobahn is running, but its interface was not built into this copy.</h1>
            <p>
                The live view is a C# application compiled to JavaScript by
                <code>Transpose</code>, which is a global tool a clean clone does not have. Build
                it with:
            </p>
            <p><code>dotnet tool update --global Transpose.Compiler</code><br>
               <code>dotnet build src/Autobahn.Ui/Autobahn.Ui.slnx</code></p>
            <p class="note">The run itself is unaffected, and the API below is live:</p>
            <ul>
                <li><code>GET /api/run</code> — what this run is</li>
                <li><code>GET /api/snapshot</code> — where it is now, with its history</li>
                <li><code>GET /api/history?from=</code> — backfill from a sequence number</li>
                <li><code>GET /api/reports</code> — the artifacts written so far</li>
                <li><code>WS /api/live</code> — one frame per reporting interval</li>
            </ul>
            <p class="note">Each needs this page's <code>token</code> query parameter, or an
               <code>X-Autobahn-Token</code> header.</p>
        </main>
        </body>
        </html>
        """;
}
