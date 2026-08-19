using Autobahn.Configuration;
using Autobahn.Internal.Services;
using Autobahn.Stats;

namespace Autobahn.Tests;

/// <summary>
/// The precedence order, exercised one layer at a time. Environment variables are process
/// state, so every test that sets one puts it back.
/// </summary>
[NotInParallel]
internal class ConfigProvenanceTests
{
    private static AutobahnContext Context(AutobahnConfig? config = null) =>
        AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("scn", _ => Task.FromResult<IResponse>(Response.Ok()))
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.Inject(1, Time.Seconds(1), Time.Seconds(5))))
        with
        { Config = config };

    private static EffectiveSetting Setting(AutobahnContext context, string name) =>
        ContextResolver.CreateSessionArgs(TestInfo.Empty, context).Value
            .EffectiveSettings.Single(x => x.Name == name);

    private static void WithEnv(string name, string? value, Action body)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);

        try { body(); }
        finally { Environment.SetEnvironmentVariable(name, previous); }
    }

    [Test]
    public async Task An_untouched_setting_reports_itself_as_a_default()
    {
        var setting = Setting(Context(), "TestSuite");

        await Assert.That(setting.Value).IsEqualTo(Constants.DefaultTestSuite);
        await Assert.That(setting.Source).IsEqualTo(ConfigSource.Default);
    }

    [Test]
    public async Task A_setting_chosen_in_code_reports_itself_as_code()
    {
        var setting = Setting(Context().WithTestSuite("checkout"), "TestSuite");

        await Assert.That(setting.Value).IsEqualTo("checkout");
        await Assert.That(setting.Source).IsEqualTo(ConfigSource.Code);
    }

    [Test]
    public async Task The_json_config_beats_code()
    {
        var config = new AutobahnConfig { TestSuite = "from-config" };
        var setting = Setting(Context(config).WithTestSuite("from-code"), "TestSuite");

        await Assert.That(setting.Value).IsEqualTo("from-config");
        await Assert.That(setting.Source).IsEqualTo(ConfigSource.JsonConfig);
    }

    [Test]
    public async Task An_environment_variable_beats_the_json_config()
    {
        var config = new AutobahnConfig { TestSuite = "from-config" };

        EffectiveSetting? setting = null;
        WithEnv("AUTOBAHN_TEST_SUITE", "from-env", () => setting = Setting(Context(config).WithTestSuite("from-code"), "TestSuite"));

        await Assert.That(setting!.Value).IsEqualTo("from-env");
        await Assert.That(setting.Source).IsEqualTo(ConfigSource.Environment);
    }

    [Test]
    public async Task The_report_folder_follows_the_same_order()
    {
        var config = new AutobahnConfig
        {
            GlobalSettings = new GlobalSettings { ReportFolder = "./from-config" }
        };

        var fromCode = Setting(Context().WithReportFolder("./from-code"), "ReportFolder");
        await Assert.That(fromCode.Source).IsEqualTo(ConfigSource.Code);

        var fromConfig = Setting(Context(config).WithReportFolder("./from-code"), "ReportFolder");
        await Assert.That(fromConfig.Value).IsEqualTo("./from-config");

        EffectiveSetting? fromEnv = null;
        WithEnv("AUTOBAHN_REPORT_FOLDER", "./from-env",
            () => fromEnv = Setting(Context(config).WithReportFolder("./from-code"), "ReportFolder"));

        await Assert.That(fromEnv!.Value).IsEqualTo("./from-env");
        await Assert.That(fromEnv.Source).IsEqualTo(ConfigSource.Environment);
    }

    [Test]
    public async Task Report_formats_come_from_the_environment_as_a_comma_separated_list()
    {
        EffectiveSetting? setting = null;

        WithEnv("AUTOBAHN_REPORT_FORMATS", "Json, Md",
            () => setting = Setting(Context(), "ReportFormats"));

        await Assert.That(setting!.Value).IsEqualTo("Json, Md");
        await Assert.That(setting.Source).IsEqualTo(ConfigSource.Environment);
    }

    [Test]
    public async Task An_unrecognised_format_in_the_environment_is_ignored_rather_than_failing_the_run()
    {
        EffectiveSetting? setting = null;

        WithEnv("AUTOBAHN_REPORT_FORMATS", "Json, Papyrus",
            () => setting = Setting(Context(), "ReportFormats"));

        await Assert.That(setting!.Value).IsEqualTo("Json");
    }

    [Test]
    public async Task A_reporting_interval_below_the_minimum_is_rejected_whatever_layer_it_came_from()
    {
        var rejected = false;

        WithEnv("AUTOBAHN_REPORTING_INTERVAL", "00:00:01",
            () => rejected = ContextResolver.CreateSessionArgs(TestInfo.Empty, Context()).IsError);

        await Assert.That(rejected).IsTrue();

        // And a valid one from the same layer is taken.
        EffectiveSetting? accepted = null;
        WithEnv("AUTOBAHN_REPORTING_INTERVAL", "00:00:30",
            () => accepted = Setting(Context(), "ReportingInterval"));

        await Assert.That(accepted!.Value).IsEqualTo("00:00:30");
        await Assert.That(accepted.Source).IsEqualTo(ConfigSource.Environment);
    }

    [Test]
    public async Task Every_resolved_setting_is_recorded_exactly_once()
    {
        var settings = ContextResolver.CreateSessionArgs(TestInfo.Empty, Context()).Value.EffectiveSettings;

        var names = settings.Select(x => x.Name).ToArray();

        await Assert.That(names.Distinct().Count()).IsEqualTo(names.Length);
        await Assert.That(names).Contains("TestSuite");
        await Assert.That(names).Contains("TestName");
        await Assert.That(names).Contains("TargetScenarios");
        await Assert.That(names).Contains("ReportFileName");
        await Assert.That(names).Contains("ReportFolder");
        await Assert.That(names).Contains("ReportFormats");
        await Assert.That(names).Contains("ReportingInterval");
        await Assert.That(names).Contains("EnableHintsAnalyzer");
    }
}

[NotInParallel]
internal class CustomSettingsTests
{
    public sealed record TargetSettings
    {
        public string TargetHost { get; set; } = "http://localhost";
        public int MsgSizeInBytes { get; set; }
        public string Tenant { get; set; } = "";
    }

    private static AutobahnConfig ConfigWith(string? global, string? scenario) => new()
    {
        GlobalSettings = new GlobalSettings
        {
            CustomSettings = global,
            ScenariosSettings = scenario is null
                ? null
                : [new ScenarioSetting { ScenarioName = "scn", CustomSettings = scenario }]
        }
    };

    private static TargetSettings Run(AutobahnConfig config)
    {
        TargetSettings? seen = null;

        var context = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("scn", async ctx =>
                    {
                        await Task.Delay(Time.Milliseconds(5), ctx.CancellationToken);
                        return Response.Ok();
                    })
                    .WithInit(ctx =>
                    {
                        seen = ctx.GetCustomSettings<TargetSettings>();
                        return Task.CompletedTask;
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.IterationsForConstant(copies: 1, iterations: 5)))
            .WithoutReports()
            .WithoutRuntimeMetrics();

        (context with { Config = config }).Run();

        return seen!;
    }

    [Test]
    public async Task Global_custom_settings_reach_every_scenario()
    {
        var settings = Run(ConfigWith("""{ "TargetHost": "https://staging", "Tenant": "acme" }""", null));

        await Assert.That(settings.TargetHost).IsEqualTo("https://staging");
        await Assert.That(settings.Tenant).IsEqualTo("acme");
    }

    [Test]
    public async Task A_scenarios_own_settings_win_key_by_key_over_the_global_ones()
    {
        var settings = Run(ConfigWith(
            """{ "TargetHost": "https://staging", "Tenant": "acme" }""",
            """{ "TargetHost": "https://scenario-specific", "MsgSizeInBytes": 1024 }"""));

        await Assert.That(settings.TargetHost).IsEqualTo("https://scenario-specific");
        await Assert.That(settings.MsgSizeInBytes).IsEqualTo(1024);

        // Not repeated in the scenario's block, so it comes through from the global one.
        await Assert.That(settings.Tenant).IsEqualTo("acme");
    }

    [Test]
    public async Task A_run_with_no_custom_settings_at_all_gets_the_types_own_defaults()
    {
        var settings = Run(new AutobahnConfig { GlobalSettings = new GlobalSettings() });

        await Assert.That(settings.TargetHost).IsEqualTo("http://localhost");
        await Assert.That(settings.MsgSizeInBytes).IsEqualTo(0);
    }

    [Test]
    public async Task Settings_that_are_not_valid_json_leave_the_scenario_with_its_defaults()
    {
        var settings = Run(ConfigWith("{ not json at all", null));

        await Assert.That(settings.TargetHost).IsEqualTo("http://localhost");
    }
}

internal class CommandLineArgsShowConfigTests
{
    [Test]
    public async Task Show_config_is_recognised_on_the_command_line()
    {
        await Assert.That(CommandLineArgs.Parse(["--show-config"]).ShowConfig).IsTrue();
        await Assert.That(CommandLineArgs.Parse(["-t", "a"]).ShowConfig).IsFalse();
    }
}
