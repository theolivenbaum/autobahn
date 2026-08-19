using Autobahn.Http;

namespace Autobahn.Tests;

public class ScenarioCodeGeneratorTests
{
    private static readonly IReadOnlyList<HttpRequest> Recorded =
    [
        HttpRequest.Get("https://shop.example.com/api/products").WithHeader("Accept", "application/json"),
        HttpRequest.Post("https://shop.example.com/api/basket").WithStringBody("""{"sku":"abc"}""", "application/json"),
        HttpRequest.Get("https://shop.example.com/api/products")
    ];

    [Test]
    public async Task The_generated_script_is_runnable_source_with_a_scenario_at_the_end()
    {
        var code = ScenarioCodeGenerator.Generate(Recorded);

        await Assert.That(code).Contains("var scenario = Scenario.Create(\"recorded\"");
        await Assert.That(code).Contains("HttpClientPool.CreateClient");
        await Assert.That(code).Contains("return scenario;");
        await Assert.That(code).Contains("autobahn run <this file>");
    }

    [Test]
    public async Task The_generated_file_says_what_still_has_to_be_done_to_it()
    {
        var code = ScenarioCodeGenerator.Generate(Recorded);

        // A recording is a starting point, and a generator that pretends otherwise produces a
        // test that measures one row of one user's data.
        await Assert.That(code).Contains("starting point, not a finished test");
        await Assert.That(code).Contains("Feed");
        await Assert.That(code).Contains("bearer token");
        await Assert.That(code).Contains(".WithCheck");
    }

    [Test]
    public async Task Every_recorded_request_becomes_a_named_step()
    {
        var code = ScenarioCodeGenerator.Generate(Recorded);

        await Assert.That(code).Contains("Step.Run(\"get_api_products\"");
        await Assert.That(code).Contains("Step.Run(\"post_api_basket\"");
    }

    [Test]
    public async Task A_repeated_call_gets_a_distinct_step_name()
    {
        // Two steps sharing a name merge into one row in the report, which is exactly the
        // confusion a step per request was meant to avoid.
        var names = ScenarioCodeGenerator.StepNames(Recorded);

        await Assert.That(names).IsEquivalentTo(new[] { "get_api_products", "post_api_basket", "get_api_products_2" });
        await Assert.That(names.Distinct().Count()).IsEqualTo(names.Length);
    }

    [Test]
    public async Task A_base_address_is_pulled_out_and_the_urls_become_relative()
    {
        var code = ScenarioCodeGenerator.Generate(
            Recorded, ScenarioCodeOptions.Default with { BaseAddress = "https://shop.example.com" });

        await Assert.That(code).Contains("BaseAddress = \"https://shop.example.com\"");
        await Assert.That(code).Contains("HttpRequest.Get(\"/api/products\")");
        await Assert.That(code).DoesNotContain("HttpRequest.Get(\"https://shop.example.com/api/products\")");
    }

    [Test]
    public async Task A_namespace_makes_it_a_class_the_cli_can_discover()
    {
        var code = ScenarioCodeGenerator.Generate(
            Recorded, ScenarioCodeOptions.Default with { Namespace = "MyTests", ClassName = "Checkout" });

        await Assert.That(code).Contains("namespace MyTests;");
        await Assert.That(code).Contains("public static class Checkout");
        await Assert.That(code).Contains("[ScenarioSource]");
        await Assert.That(code).Contains("using Autobahn.Http;");
    }

    [Test]
    public async Task A_recorded_body_is_written_out_and_flagged()
    {
        var code = ScenarioCodeGenerator.Generate(Recorded);

        // Written as a verbatim literal, because a recorded body is full of quotes and a
        // generator that emits something that will not compile has saved nobody anything.
        await Assert.That(code).Contains(ScenarioCodeGenerator.Literal("""{"sku":"abc"}"""));
        await Assert.That(code).Contains("TODO: this body was recorded once");
    }

    [Test]
    [Arguments("plain", "\"plain\"")]
    [Arguments("has \"quotes\"", "@\"has \"\"quotes\"\"\"")]
    [Arguments("back\\slash", "@\"back\\slash\"")]
    public async Task Recorded_text_becomes_a_literal_that_compiles(string value, string expected)
    {
        // Recorded bodies are full of quotes and backslashes, and a generator that emits
        // something that will not compile has saved nobody anything.
        await Assert.That(ScenarioCodeGenerator.Literal(value)).IsEqualTo(expected);
    }

    [Test]
    public async Task Generating_from_nothing_says_so()
    {
        var ex = Assert.Throws<AutobahnException>(() => ScenarioCodeGenerator.Generate([]));

        await Assert.That(ex!.Message).Contains("no requests");
    }

    [Test]
    public async Task A_har_recording_goes_straight_to_source()
    {
        const string har = """
            { "log": { "entries": [
              { "request": { "method": "GET", "url": "https://x.example.com/api/thing",
                  "headers": [ { "name": "accept", "value": "application/json" } ] },
                "response": { "status": 200, "content": { "mimeType": "application/json" } } }
            ] } }
            """;

        var code = ScenarioCodeGenerator.FromHar(har, ScenarioCodeOptions.Default with { ScenarioName = "from_har" });

        await Assert.That(code).Contains("Scenario.Create(\"from_har\"");
        await Assert.That(code).Contains("/api/thing");
    }
}
