using Microsoft.Extensions.Logging;
using Autobahn.Cli;
using Autobahn.Stats;

namespace Autobahn.Tests;

internal class CliParserTests
{
    [Test]
    public async Task No_arguments_asks_for_help()
    {
        await Assert.That(CliParser.Parse([]).Command).IsEqualTo("help");
    }

    [Test]
    [Arguments("-h")]
    [Arguments("--help")]
    [Arguments("help")]
    public async Task Help_is_spelled_three_ways(string arg)
    {
        await Assert.That(CliParser.Parse([arg]).Command).IsEqualTo("help");
    }

    [Test]
    [Arguments("-v")]
    [Arguments("--version")]
    [Arguments("version")]
    public async Task Version_is_spelled_three_ways(string arg)
    {
        await Assert.That(CliParser.Parse([arg]).Command).IsEqualTo("version");
    }

    [Test]
    public async Task An_unknown_command_says_which_ones_exist()
    {
        var options = CliParser.Parse(["frobnicate", "x.dll"]);

        await Assert.That(options.Error).IsNotNull();
        await Assert.That(options.Error!).Contains("frobnicate");
        await Assert.That(options.Error!).Contains("run, list");
    }

    [Test]
    public async Task An_option_where_a_command_belongs_says_so()
    {
        var options = CliParser.Parse(["--out", "./x"]);

        await Assert.That(options.Error!).Contains("is an option, not a command");
    }

    [Test]
    public async Task A_command_with_no_file_says_it_needs_one()
    {
        await Assert.That(CliParser.Parse(["run"]).Error!).Contains("needs a file");
    }

    [Test]
    public async Task Run_takes_the_file_and_every_option()
    {
        var options = CliParser.Parse(
        [
            "run", "tests.dll",
            "-t", "read", "--target", "write",
            "-c", "cfg.json",
            "-i", "infra.json",
            "-o", "./out",
            "-n", "custom",
            "-f", "Json,Md",
            "-l", "Warning",
            "--suite", "checkout",
            "--test-name", "peak",
            "--reporting-interval", "00:00:10",
            "--show-config",
            "--no-runtime-metrics",
            "--no-reports"
        ]);

        await Assert.That(options.Error).IsNull();
        await Assert.That(options.Command).IsEqualTo("run");
        await Assert.That(options.Source).IsEqualTo("tests.dll");
        await Assert.That(options.TargetScenarios).IsEquivalentTo(new[] { "read", "write" });
        await Assert.That(options.ConfigPath).IsEqualTo("cfg.json");
        await Assert.That(options.InfraConfigPath).IsEqualTo("infra.json");
        await Assert.That(options.ReportFolder).IsEqualTo("./out");
        await Assert.That(options.ReportFileName).IsEqualTo("custom");
        await Assert.That(options.ReportFormats).IsEquivalentTo(new[] { ReportFormat.Json, ReportFormat.Md });
        await Assert.That(options.MinimumLogLevel).IsEqualTo(LogLevel.Warning);
        await Assert.That(options.TestSuite).IsEqualTo("checkout");
        await Assert.That(options.TestName).IsEqualTo("peak");
        await Assert.That(options.ReportingInterval).IsEqualTo(Time.Seconds(10));
        await Assert.That(options.ShowConfig).IsTrue();
        await Assert.That(options.NoRuntimeMetrics).IsTrue();
        await Assert.That(options.NoReports).IsTrue();
    }

    [Test]
    public async Task Options_take_their_value_with_an_equals_sign_too()
    {
        var options = CliParser.Parse(["run", "tests.dll", "--out=./here", "--format=Csv"]);

        await Assert.That(options.ReportFolder).IsEqualTo("./here");
        await Assert.That(options.ReportFormats).IsEquivalentTo(new[] { ReportFormat.Csv });
    }

    [Test]
    public async Task An_unknown_option_is_an_error_rather_than_being_ignored()
    {
        // Unlike the in-process parser, where an unrecognised argument belongs to the test
        // runner: at a prompt, a mistyped flag that silently does nothing is worse.
        var options = CliParser.Parse(["run", "tests.dll", "--verbose"]);

        await Assert.That(options.Error!).Contains("--verbose");
    }

    [Test]
    public async Task An_option_missing_its_value_says_which_one()
    {
        await Assert.That(CliParser.Parse(["run", "tests.dll", "--out"]).Error!).Contains("--out");
    }

    [Test]
    public async Task An_unknown_report_format_lists_the_known_ones()
    {
        var options = CliParser.Parse(["run", "tests.dll", "-f", "Papyrus"]);

        await Assert.That(options.Error!).Contains("Papyrus");
        await Assert.That(options.Error!).Contains("Json");
    }

    [Test]
    public async Task An_unparseable_duration_says_what_one_looks_like()
    {
        var options = CliParser.Parse(["run", "tests.dll", "--reporting-interval", "soon"]);

        await Assert.That(options.Error!).Contains("soon");
        await Assert.That(options.Error!).Contains("00:00:10");
    }

    [Test]
    public async Task A_second_file_is_an_error_rather_than_the_last_one_winning()
    {
        var options = CliParser.Parse(["run", "one.dll", "two.dll"]);

        await Assert.That(options.Error!).Contains("second source");
    }
}

internal class ScriptScenarioLoaderTests
{
    private static string WriteScript(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"autobahn_script_{Guid.NewGuid():N}.csx");
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    [Arguments("x.cs", true)]
    [Arguments("x.csx", true)]
    [Arguments("x.dll", false)]
    [Arguments("x", false)]
    public async Task A_script_is_recognised_by_its_extension(string path, bool isScript)
    {
        await Assert.That(ScriptScenarioLoader.IsScript(path)).IsEqualTo(isScript);
    }

    [Test]
    public async Task A_script_that_returns_one_scenario_yields_one()
    {
        var path = WriteScript("""
            return Scenario.Create("from_script", async ctx =>
                {
                    await Task.Delay(1, ctx.CancellationToken);
                    return Response.Ok();
                })
                .WithoutWarmUp()
                .WithLoadSimulations(Simulation.IterationsForConstant(1, 5));
            """);

        try
        {
            var scenarios = await ScriptScenarioLoader.LoadAsync(path);

            await Assert.That(scenarios.Count).IsEqualTo(1);
            await Assert.That(scenarios[0].ScenarioName).IsEqualTo("from_script");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_script_can_return_several_scenarios()
    {
        var path = WriteScript("""
            ScenarioProps Make(string name) =>
                Scenario.Create(name, _ => Task.FromResult<IResponse>(Response.Ok()))
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.IterationsForConstant(1, 5));

            return new[] { Make("a"), Make("b") };
            """);

        try
        {
            var scenarios = await ScriptScenarioLoader.LoadAsync(path);

            await Assert.That(scenarios.Select(x => x.ScenarioName)).IsEquivalentTo(new[] { "a", "b" });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task The_scripts_imports_cover_the_public_surface_without_a_using_directive()
    {
        var path = WriteScript("""
            var ids = Feed.Circular("ids", new[] { 1, 2, 3 });

            return Scenario.Create("imports", async ctx =>
                {
                    ctx.Metrics.Counter("c").Increment();
                    await Task.Delay(1, ctx.CancellationToken);
                    return Response.Ok(statusCode: ids.Next().ToString());
                })
                .WithoutWarmUp()
                .WithLoadSimulations(Simulation.IterationsForConstant(1, 5));
            """);

        try
        {
            var scenarios = await ScriptScenarioLoader.LoadAsync(path);

            await Assert.That(scenarios.Count).IsEqualTo(1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_script_that_will_not_compile_reports_the_diagnostics()
    {
        var path = WriteScript("return this is not C#;");

        try
        {
            var ex = await Assert.ThrowsAsync<AutobahnException>(async () => await ScriptScenarioLoader.LoadAsync(path));

            await Assert.That(ex!.Message).Contains("Could not compile");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_script_that_returns_nothing_says_what_it_should_have_returned()
    {
        var path = WriteScript("var x = 1;");

        try
        {
            var ex = await Assert.ThrowsAsync<AutobahnException>(async () => await ScriptScenarioLoader.LoadAsync(path));

            await Assert.That(ex!.Message).Contains("last expression");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_script_that_returns_the_wrong_thing_says_what_it_returned()
    {
        var path = WriteScript("return 42;");

        try
        {
            var ex = await Assert.ThrowsAsync<AutobahnException>(async () => await ScriptScenarioLoader.LoadAsync(path));

            await Assert.That(ex!.Message).Contains("Int32");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_missing_script_says_which_file()
    {
        var missing = Path.Combine(Path.GetTempPath(), "autobahn_no_such_script.csx");

        var ex = await Assert.ThrowsAsync<AutobahnException>(async () => await ScriptScenarioLoader.LoadAsync(missing));

        await Assert.That(ex!.Message).Contains("autobahn_no_such_script.csx");
    }
}

internal class AssemblyScenarioLoaderTests
{
    /// <summary>Scenario sources this test assembly exposes, for the loader to find.</summary>
    public static class Sources
    {
        [ScenarioSource]
        public static ScenarioProps Marked =>
            Scenario.Create("marked", _ => Task.FromResult<IResponse>(Response.Ok()))
                .WithoutWarmUp()
                .WithLoadSimulations(Simulation.IterationsForConstant(1, 5));

        [ScenarioSource]
        public static IEnumerable<ScenarioProps> Several()
        {
            yield return Scenario.Create("several_a", _ => Task.FromResult<IResponse>(Response.Ok()))
                .WithoutWarmUp()
                .WithLoadSimulations(Simulation.IterationsForConstant(1, 5));

            yield return Scenario.Create("several_b", _ => Task.FromResult<IResponse>(Response.Ok()))
                .WithoutWarmUp()
                .WithLoadSimulations(Simulation.IterationsForConstant(1, 5));
        }

        /// <summary>Unmarked, so it is skipped while the marked ones exist.</summary>
        public static ScenarioProps Unmarked() =>
            Scenario.Create("unmarked", _ => Task.FromResult<IResponse>(Response.Ok()))
                .WithoutWarmUp()
                .WithLoadSimulations(Simulation.IterationsForConstant(1, 5));
    }

    [Test]
    public async Task The_loader_finds_marked_sources_and_skips_the_rest()
    {
        var path = typeof(AssemblyScenarioLoaderTests).Assembly.Location;

        var names = AssemblyScenarioLoader.Load(path).Select(x => x.ScenarioName).ToArray();

        await Assert.That(names).Contains("marked");
        await Assert.That(names).Contains("several_a");
        await Assert.That(names).Contains("several_b");
        await Assert.That(names).DoesNotContain("unmarked");
    }

    [Test]
    public async Task A_missing_assembly_says_which_file()
    {
        var missing = Path.Combine(Path.GetTempPath(), "autobahn_no_such_assembly.dll");

        var ex = Assert.Throws<AutobahnException>(() => AssemblyScenarioLoader.Load(missing));

        await Assert.That(ex!.Message).Contains("autobahn_no_such_assembly.dll");
    }

    [Test]
    public async Task An_assembly_with_no_scenarios_says_what_one_looks_like()
    {
        // Any assembly that does not expose scenarios will do; the engine itself is one.
        var path = typeof(AutobahnRunner).Assembly.Location;

        var ex = Assert.Throws<AutobahnException>(() => AssemblyScenarioLoader.Load(path));

        await Assert.That(ex!.Message).Contains("No scenarios found");
        await Assert.That(ex.Message).Contains("ScenarioSource");
    }
}

internal class RecordCliParsingTests
{
    [Test]
    public async Task Record_is_a_command()
    {
        var options = CliParser.Parse(["record", "https://example.com"]);

        await Assert.That(options.Error).IsNull();
        await Assert.That(options.Command).IsEqualTo("record");
        await Assert.That(options.Source).IsEqualTo("https://example.com");

        // Same-origin is the default: a page pulls in fonts, analytics and embeds from half
        // the internet, and none of it is what the test is about.
        await Assert.That(options.SameOriginOnly).IsTrue();
        await Assert.That(options.IncludeAssets).IsFalse();
        await Assert.That(options.Headless).IsFalse();
    }

    [Test]
    public async Task Record_takes_its_own_options()
    {
        var options = CliParser.Parse(
        [
            "record", "https://example.com",
            "--headless", "--include-assets", "--all-origins", "--keep-browser-headers",
            "--namespace", "MyTests",
            "--browser-path", "/opt/chromium",
            "-n", "Out.cs"
        ]);

        await Assert.That(options.Error).IsNull();
        await Assert.That(options.Headless).IsTrue();
        await Assert.That(options.IncludeAssets).IsTrue();
        await Assert.That(options.SameOriginOnly).IsFalse();
        await Assert.That(options.KeepBrowserHeaders).IsTrue();
        await Assert.That(options.RecordNamespace).IsEqualTo("MyTests");
        await Assert.That(options.BrowserPath).IsEqualTo("/opt/chromium");
        await Assert.That(options.ReportFileName).IsEqualTo("Out.cs");
    }

    [Test]
    public async Task Record_with_no_url_says_it_needs_one()
    {
        await Assert.That(CliParser.Parse(["record"]).Error!).Contains("needs a URL");
    }

    [Test]
    public async Task The_unknown_command_message_lists_record_too()
    {
        await Assert.That(CliParser.Parse(["frobnicate", "x"]).Error!).Contains("record");
    }
}

/// <summary>
/// The generator's output has to compile, and the runner has to accept it. Checking the shape
/// of the text is not enough: a generated file that does not run is worse than none.
/// </summary>
internal class GeneratedScenarioRunsTests
{
    [Test]
    public async Task A_generated_script_compiles_and_loads_through_the_runner()
    {
        var code = Autobahn.Http.ScenarioCodeGenerator.Generate(
        [
            Autobahn.Http.HttpRequest.Get("https://example.com/api/products"),
            Autobahn.Http.HttpRequest.Post("https://example.com/api/basket")
                .WithStringBody("""{"sku":"abc"}""", "application/json")
        ],
        new Autobahn.Http.ScenarioCodeOptions
        {
            ScenarioName = "generated",
            BaseAddress = "https://example.com"
        });

        var path = Path.Combine(Path.GetTempPath(), $"autobahn_generated_{Guid.NewGuid():N}.csx");
        await File.WriteAllTextAsync(path, code);

        try
        {
            var scenarios = await ScriptScenarioLoader.LoadAsync(path);

            await Assert.That(scenarios.Count).IsEqualTo(1);
            await Assert.That(scenarios[0].ScenarioName).IsEqualTo("generated");
            await Assert.That(scenarios[0].LoadSimulations).IsNotEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task A_generated_class_says_what_assembly_discovery_looks_for()
    {
        var code = Autobahn.Http.ScenarioCodeGenerator.Generate(
            [Autobahn.Http.HttpRequest.Get("https://example.com/api/thing")],
            new Autobahn.Http.ScenarioCodeOptions { Namespace = "Generated.Tests", ClassName = "Thing" });

        // A class form cannot be loaded without compiling a project, so what is checked here
        // is that it says the things assembly discovery looks for.
        await Assert.That(code).Contains("[ScenarioSource]");
        await Assert.That(code).Contains("public static ScenarioProps Build()");
        await Assert.That(code).Contains("return scenario;");
    }
}
