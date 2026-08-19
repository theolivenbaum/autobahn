using System.Text.Json;
using Autobahn.Configuration;
using Autobahn.Internal.Json;

namespace Autobahn.Tests;

internal class JsonConfigTests
{
    private const string ConfigFolder = "Assets/Configuration";

    private static AutobahnConfig Read(string fileName) =>
        AutobahnJson.Deserialize<AutobahnConfig>(File.ReadAllText(Path.Combine(ConfigFolder, fileName)));

    public sealed record TestCustomSettings(string TargetHost, int MsgSizeInBytes, int PauseMs);

    [Test]
    [Arguments("test_config.json")]
    [Arguments("test_config_2.json")]
    [Arguments("scenario_init_only_config.json")]
    [Arguments("duplicate_scenarios_config.json")]
    public async Task A_well_formed_config_file_parses(string fileName)
    {
        var config = Read(fileName);

        await Assert.That(config.GlobalSettings).IsNotNull();
    }

    [Test]
    public async Task A_scenario_settings_block_with_no_scenario_name_is_rejected()
    {
        // "ScenarioName" is required: a settings block that names no scenario cannot be applied.
        await Assert.That(() => Read("missing_fields_config.json")).Throws<JsonException>();
    }

    [Test]
    public async Task Custom_settings_survive_as_raw_json_the_scenario_can_bind()
    {
        var config = Read("test_config.json");
        var rawSettings = config.GlobalSettings!.ScenariosSettings![0].CustomSettings;

        await Assert.That(rawSettings).IsNotNull();

        var settings = JsonSerializer.Deserialize<TestCustomSettings>(rawSettings!, AutobahnJson.Config)!;

        await Assert.That(settings.TargetHost).IsEqualTo("localhost");
        await Assert.That(settings.MsgSizeInBytes).IsEqualTo(1000);
        await Assert.That(settings.PauseMs).IsEqualTo(100);
    }

    [Test]
    public async Task Load_simulations_parse_into_the_right_cases()
    {
        var config = Read("test_config.json");
        var simulations = config.GlobalSettings!.ScenariosSettings![0].LoadSimulationsSettings!;

        await Assert.That(simulations.Count).IsEqualTo(4);
        await Assert.That(simulations[0]).IsEqualTo(Simulation.RampingConstant(2, Time.Seconds(2)));
        await Assert.That(simulations[1]).IsEqualTo(Simulation.KeepConstant(2, Time.Seconds(2)));
        await Assert.That(simulations[2]).IsEqualTo(Simulation.RampingInject(2, Time.Seconds(1), Time.Seconds(2)));
        await Assert.That(simulations[3]).IsEqualTo(Simulation.Inject(2, Time.Seconds(1), Time.Seconds(2)));
    }

    [Test]
    public async Task Report_formats_and_durations_parse()
    {
        var settings = Read("test_config.json").GlobalSettings!;

        await Assert.That(settings.ReportFormats).IsEquivalentTo(new[] { Stats.ReportFormat.Html, Stats.ReportFormat.Txt });
        await Assert.That(settings.ReportingInterval).IsEqualTo(Time.Seconds(30));
        await Assert.That(settings.ReportFolder).IsEqualTo("./my_reports");
    }

    [Test]
    public async Task An_unknown_load_simulation_name_is_rejected()
    {
        const string json = """{ "GlobalSettings": { "ScenariosSettings": [ { "ScenarioName": "a", "LoadSimulationsSettings": [ { "TeleportUsers": [1, "00:00:01"] } ] } ] } }""";

        await Assert.That(() => AutobahnJson.Deserialize<AutobahnConfig>(json)).Throws<JsonException>();
    }

    [Test]
    public async Task A_load_simulation_with_the_wrong_argument_count_is_rejected()
    {
        const string json = """{ "GlobalSettings": { "ScenariosSettings": [ { "ScenarioName": "a", "LoadSimulationsSettings": [ { "KeepConstant": [1] } ] } ] } }""";

        await Assert.That(() => AutobahnJson.Deserialize<AutobahnConfig>(json)).Throws<JsonException>();
    }

    [Test]
    public async Task Settings_Autobahn_does_not_know_about_are_ignored()
    {
        const string json = """{ "TestSuite": "s", "SomethingElse": { "a": 1 }, "GlobalSettings": { "Nonsense": true } }""";

        var config = AutobahnJson.Deserialize<AutobahnConfig>(json);

        await Assert.That(config.TestSuite).IsEqualTo("s");
    }
}

public class ConfigLoadingTests
{
    private const string ConfigFolder = "Assets/Configuration";

    [Test]
    public async Task LoadConfig_reads_a_json_file()
    {
        var context = AutobahnRunner
            .RegisterScenarios(Scenario.Create("scenario_1", _ => Task.FromResult<IResponse>(Response.Ok())))
            .LoadConfig(Path.Combine(ConfigFolder, "test_config.json"));

        await Assert.That(context.Config).IsNotNull();
        await Assert.That(context.Config!.TestSuite).IsEqualTo("gitter.io");
    }

    [Test]
    public async Task LoadConfig_rejects_a_file_that_carries_no_Autobahn_settings()
    {
        var context = AutobahnRunner
            .RegisterScenarios(Scenario.Create("scenario_1", _ => Task.FromResult<IResponse>(Response.Ok())));

        await Assert.That(() => context.LoadConfig(Path.Combine(ConfigFolder, "empty_config.json")))
            .Throws<AutobahnException>();
    }

    [Test]
    public async Task LoadInfraConfig_reads_a_json_file()
    {
        var context = AutobahnRunner.RegisterScenarios().WithoutReports()
            .LoadInfraConfig(Path.Combine(ConfigFolder, "infra_config.json"));

        await Assert.That(context.InfraConfig).IsNotNull();
        await Assert.That(context.InfraConfig!.GetSection("Logging:LogLevel:Default").Value).IsEqualTo("Debug");
    }

    [Test]
    public async Task LoadInfraConfig_fails_loudly_when_the_file_is_missing()
    {
        var context = AutobahnRunner.RegisterScenarios().WithoutReports();

        await Assert.That(() => context.LoadInfraConfig(Path.Combine(ConfigFolder, "does_not_exist.json")))
            .Throws<FileNotFoundException>();
    }

    [Test]
    public async Task LoadConfig_fails_loudly_when_the_file_is_missing()
    {
        var context = AutobahnRunner.RegisterScenarios();

        await Assert.That(() => context.LoadConfig(Path.Combine(ConfigFolder, "does_not_exist.json")))
            .Throws<FileNotFoundException>();
    }

    [Test]
    public async Task A_config_in_a_format_Autobahn_does_not_read_is_rejected()
    {
        var context = AutobahnRunner.RegisterScenarios();

        await Assert.That(() => context.LoadConfig("config.yaml")).Throws<AutobahnException>();
    }
}
