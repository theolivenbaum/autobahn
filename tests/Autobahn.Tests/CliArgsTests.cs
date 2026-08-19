using Autobahn.Internal.Services;

namespace Autobahn.Tests;

public class CliArgsTests
{
    private const string ConfigFolder = "Assets/Configuration";

    private static ScenarioProps Scn(string name) =>
        Scenario.Create(name, _ => Task.FromResult<IResponse>(Response.Ok())).WithoutWarmUp();

    private static AutobahnContext Context() => AutobahnRunner.RegisterScenarios(Scn("scenario"), Scn("scenario2"));

    [Test]
    [Arguments("-c")]
    [Arguments("--config")]
    public async Task The_config_flag_loads_the_json_config(string command)
    {
        var context = Context().ExecuteCliArgs([command, Path.Combine(ConfigFolder, "test_config.json")]);

        await Assert.That(context.Config).IsNotNull();
        await Assert.That(context.InfraConfig).IsNull();
    }

    [Test]
    public async Task The_config_flag_also_accepts_an_inline_value()
    {
        var context = Context().ExecuteCliArgs([$"--config={Path.Combine(ConfigFolder, "test_config.json")}"]);

        await Assert.That(context.Config).IsNotNull();
    }

    [Test]
    [Arguments("-C")]
    [Arguments("--Config")]
    [Arguments("")]
    [Arguments("-")]
    [Arguments("--")]
    [Arguments("-w")]
    public async Task An_argument_that_is_not_the_config_flag_loads_nothing(string command)
    {
        var context = Context().ExecuteCliArgs([command, Path.Combine(ConfigFolder, "test_config.json")]);

        await Assert.That(context.Config).IsNull();
        await Assert.That(context.InfraConfig).IsNull();
    }

    [Test]
    [Arguments("-i")]
    [Arguments("--infra")]
    public async Task The_infra_flag_loads_the_infra_config(string command)
    {
        var context = Context().ExecuteCliArgs([command, Path.Combine(ConfigFolder, "infra_config.json")]);

        await Assert.That(context.Config).IsNull();
        await Assert.That(context.InfraConfig).IsNotNull();
    }

    [Test]
    [Arguments("-I")]
    [Arguments("--Infra")]
    [Arguments("")]
    [Arguments("-w")]
    public async Task An_argument_that_is_not_the_infra_flag_loads_nothing(string command)
    {
        var context = Context().ExecuteCliArgs([command, Path.Combine(ConfigFolder, "infra_config.json")]);

        await Assert.That(context.Config).IsNull();
        await Assert.That(context.InfraConfig).IsNull();
    }

    [Test]
    [Arguments("-c")]
    [Arguments("--config")]
    public async Task A_config_file_that_is_not_there_fails_loudly(string command)
    {
        await Assert.That(() => Context().ExecuteCliArgs([command, "not_found_config.json"]))
            .Throws<FileNotFoundException>();
    }

    [Test]
    [Arguments("-i")]
    [Arguments("--infra")]
    public async Task An_infra_config_file_that_is_not_there_fails_loudly(string command)
    {
        await Assert.That(() => Context().ExecuteCliArgs([command, "not_found_infra_config.json"]))
            .Throws<FileNotFoundException>();
    }

    [Test]
    [Arguments("-t")]
    [Arguments("--target")]
    public async Task The_target_flag_narrows_the_scenarios(string command)
    {
        var context = Context().ExecuteCliArgs([command, "scenario2"]);

        await Assert.That(context.TargetScenarios).IsNotNull();
        await Assert.That(context.TargetScenarios!.Count).IsEqualTo(1);
        await Assert.That(context.TargetScenarios[0]).IsEqualTo("scenario2");
    }

    [Test]
    public async Task The_target_flag_can_be_repeated()
    {
        var context = Context().ExecuteCliArgs(["-t", "scenario", "-t", "scenario2"]);

        await Assert.That(context.TargetScenarios).IsEquivalentTo(new[] { "scenario", "scenario2" });
    }

    [Test]
    public async Task A_flag_with_no_value_at_the_end_is_ignored()
    {
        var context = Context().ExecuteCliArgs(["--config"]);

        await Assert.That(context.Config).IsNull();
    }

    [Test]
    public async Task Parsing_reads_all_three_flags_together()
    {
        var args = CommandLineArgs.Parse(["--config", "a.json", "--infra", "b.json", "--target", "s1", "-t", "s2"]);

        await Assert.That(args.Config).IsEqualTo("a.json");
        await Assert.That(args.InfraConfig).IsEqualTo("b.json");
        await Assert.That(args.TargetScenarios).IsEquivalentTo(new[] { "s1", "s2" });
    }
}
