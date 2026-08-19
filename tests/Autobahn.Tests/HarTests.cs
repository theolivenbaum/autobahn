using Autobahn.Http;

namespace Autobahn.Tests;

public class HarTests
{
    private const string Recording = """
        {
          "log": {
            "entries": [
              {
                "request": {
                  "method": "GET",
                  "url": "https://shop.example.com/api/products",
                  "headers": [
                    { "name": ":authority", "value": "shop.example.com" },
                    { "name": "accept", "value": "application/json" },
                    { "name": "cookie", "value": "session=abc123" },
                    { "name": "authorization", "value": "Bearer recorded-token" },
                    { "name": "content-length", "value": "0" }
                  ]
                },
                "response": { "status": 200, "content": { "mimeType": "application/json" } }
              },
              {
                "request": {
                  "method": "POST",
                  "url": "https://shop.example.com/api/basket",
                  "headers": [ { "name": "content-type", "value": "application/json" } ],
                  "postData": { "mimeType": "application/json; charset=utf-8", "text": "{\"sku\":\"abc\"}" }
                },
                "response": { "status": 201, "content": { "mimeType": "application/json" } }
              },
              {
                "request": { "method": "GET", "url": "https://cdn.example.com/logo.png", "headers": [] },
                "response": { "status": 200, "content": { "mimeType": "image/png" } }
              },
              {
                "request": { "method": "GET", "url": "https://cdn.example.com/app.js", "headers": [] },
                "response": { "status": 200, "content": { "mimeType": "text/javascript" } }
              },
              {
                "request": { "method": "GET", "url": "https://shop.example.com/api/gone", "headers": [] },
                "response": { "status": 404, "content": { "mimeType": "application/json" } }
              }
            ]
          }
        }
        """;

    [Test]
    public async Task Static_assets_are_left_out_by_default()
    {
        var requests = Har.Parse(Recording);

        // A page load is mostly assets; replaying them measures the CDN and buries the two
        // requests that were the point.
        await Assert.That(requests.Select(x => x.Url))
            .IsEquivalentTo(new[]
            {
                "https://shop.example.com/api/products",
                "https://shop.example.com/api/basket"
            });
    }

    [Test]
    public async Task A_failed_recorded_response_is_left_out_by_default()
    {
        var withFailures = Har.Parse(Recording, Har.HarFilter.Default with { OnlySuccessful = false });

        await Assert.That(withFailures.Select(x => x.Url)).Contains("https://shop.example.com/api/gone");
        await Assert.That(Har.Parse(Recording).Select(x => x.Url)).DoesNotContain("https://shop.example.com/api/gone");
    }

    [Test]
    public async Task Keeping_the_assets_keeps_them()
    {
        var requests = Har.Parse(Recording, Har.HarFilter.Default with { SkipStaticAssets = false });

        await Assert.That(requests.Count).IsEqualTo(4);
    }

    [Test]
    public async Task The_recordings_own_session_is_not_carried_over()
    {
        var request = Har.Parse(Recording).First();

        var headerNames = HeaderNames(request);

        // A recorded cookie and bearer token are one session's, and replaying them across a
        // load test is the single most common way a converted recording quietly stops testing
        // anything. They have to come from a feed instead.
        await Assert.That(headerNames).DoesNotContain("cookie");
        await Assert.That(headerNames).DoesNotContain("authorization");
        await Assert.That(headerNames).DoesNotContain("content-length");

        // HTTP/2 pseudo-headers describe the frame, not the request.
        await Assert.That(headerNames.Any(x => x.StartsWith(':'))).IsFalse();

        await Assert.That(headerNames).Contains("accept");
    }

    [Test]
    public async Task Method_url_and_body_come_through()
    {
        var basket = Har.Parse(Recording).Single(x => x.Url.EndsWith("/basket"));

        await Assert.That(basket.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(basket.Url).IsEqualTo("https://shop.example.com/api/basket");
        await Assert.That(basket.CreateContent).IsNotNull();

        using var content = basket.CreateContent!();

        await Assert.That(await content.ReadAsStringAsync()).IsEqualTo("""{"sku":"abc"}""");
        await Assert.That(content.Headers.ContentType!.MediaType).IsEqualTo("application/json");
    }

    [Test]
    public async Task A_url_filter_narrows_the_conversion()
    {
        var onlyBasket = Har.Parse(Recording, Har.HarFilter.Default with { UrlContains = ["basket"] });
        var withoutBasket = Har.Parse(Recording, Har.HarFilter.Default with { UrlExcludes = ["basket"] });

        await Assert.That(onlyBasket.Count).IsEqualTo(1);
        await Assert.That(withoutBasket.Select(x => x.Url)).DoesNotContain("https://shop.example.com/api/basket");
    }

    [Test]
    public async Task An_asset_with_no_recorded_mime_type_is_still_recognised_by_its_extension()
    {
        const string byExtension = """
            { "log": { "entries": [
              { "request": { "method": "GET", "url": "https://x.example.com/a/style.css", "headers": [] },
                "response": { "status": 200 } },
              { "request": { "method": "GET", "url": "https://x.example.com/api/thing", "headers": [] },
                "response": { "status": 200 } }
            ] } }
            """;

        var requests = Har.Parse(byExtension);

        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0].Url).IsEqualTo("https://x.example.com/api/thing");
    }

    [Test]
    public async Task Something_that_is_not_a_har_says_so()
    {
        var ex = Assert.Throws<AutobahnException>(() => Har.Parse("not json"));

        await Assert.That(ex!.Message).Contains("HAR");
    }

    [Test]
    public async Task An_empty_recording_says_so()
    {
        var ex = Assert.Throws<AutobahnException>(() => Har.Parse("""{ "log": { "entries": [] } }"""));

        await Assert.That(ex!.Message).Contains("no entries");
    }

    [Test]
    public async Task A_missing_file_says_which_one()
    {
        var missing = Path.Combine(Path.GetTempPath(), "autobahn_no_such.har");

        await Assert.That(Assert.Throws<AutobahnException>(() => Har.FromFile(missing))!.Message).Contains("no_such.har");
    }

    [Test]
    public async Task A_recording_reads_from_a_file_too()
    {
        var path = Path.Combine(Path.GetTempPath(), $"autobahn_{Guid.NewGuid():N}.har");
        await File.WriteAllTextAsync(path, Recording);

        try
        {
            await Assert.That(Har.FromFile(path).Count).IsEqualTo(2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string[] HeaderNames(HttpRequest request) =>
        request.Headers.Select(x => x.Name.ToLowerInvariant()).ToArray();
}
