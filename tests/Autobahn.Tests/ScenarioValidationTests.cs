using Autobahn.Configuration;
using Autobahn.Internal;
using Autobahn.Internal.Domain;

namespace Autobahn.Tests;

internal class ScenarioValidationTests
{
    private static ScenarioProps Scn(string name) =>
        Scenario.Create(name, _ => Task.FromResult<IResponse>(Response.Ok()));

    [Test]
    public async Task Scenario_settings_override_the_scenario_when_the_name_matches()
    {
        var warmUp = Time.Seconds(30);
        var configuredDuration = Time.Seconds(50);

        var settings = new ScenarioSetting
        {
            ScenarioName = "scenario_1",
            WarmUpDuration = warmUp,
            LoadSimulationsSettings = [Simulation.KeepConstant(10, configuredDuration)],
            CustomSettings = "some data",
            MaxFailCount = Constants.ScenarioMaxFailCount
        };

        var original = ScenarioFactory.CreateScenarios(
        [
            Scn("scenario_1").WithLoadSimulations(Simulation.RampingConstant(500, Time.Seconds(80)))
        ]).Value;

        var updated = ScenarioFactory.ApplySettings([settings], original);

        await Assert.That(updated[0].PlanedDuration).IsEqualTo(configuredDuration);
        await Assert.That(updated[0].WarmUpDuration).IsEqualTo(warmUp);
        await Assert.That(updated[0].CustomSettings).IsEqualTo("some data");
        await Assert.That(updated[0].MaxFailCount).IsEqualTo(Constants.ScenarioMaxFailCount);
    }

    [Test]
    public async Task Scenario_settings_for_another_scenario_are_left_alone()
    {
        var ownDuration = Time.Seconds(5);

        var settings = new ScenarioSetting
        {
            ScenarioName = "scenario_1",
            WarmUpDuration = Time.Seconds(30),
            LoadSimulationsSettings = [Simulation.RampingConstant(5, Time.Seconds(50))],
            CustomSettings = null,
            MaxFailCount = Constants.ScenarioMaxFailCount
        };

        var original = ScenarioFactory.CreateScenarios(
        [
            Scn("scenario_2").WithoutWarmUp().WithLoadSimulations(Simulation.KeepConstant(500, ownDuration))
        ]).Value;

        var updated = ScenarioFactory.ApplySettings([settings], original);

        await Assert.That(updated[0].WarmUpDuration).IsNull();
        await Assert.That(updated[0].PlanedDuration).IsEqualTo(ownDuration);
    }

    [Test]
    public async Task Applying_settings_to_no_scenarios_produces_no_scenarios()
    {
        await Assert.That(ScenarioFactory.ApplySettings([], [])).IsEmpty();
    }

    [Test]
    public async Task A_scenario_with_a_blank_name_is_rejected()
    {
        await Assert.That(ScenarioFactory.CheckEmptyScenarioName(Scn(" ")).Error)
            .IsTypeOf<ScenarioError.EmptyScenarioName>();
    }

    [Test]
    public async Task Two_scenarios_with_the_same_name_are_rejected()
    {
        var result = ScenarioFactory.CheckDuplicateScenarioName([Scn("1"), Scn("1")]);

        await Assert.That(result.Error).IsTypeOf<ScenarioError.DuplicateScenarioName>();
    }

    [Test]
    public async Task A_warm_up_longer_than_the_scenario_is_rejected()
    {
        var scenario = Scn("1")
            .WithWarmUpDuration(Time.Seconds(5))
            .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(2)));

        var result = ScenarioFactory.CreateScenario(scenario);

        await Assert.That(result.Error).IsTypeOf<ScenarioError.WarmUpDurationIsBiggerScnDuration>();
    }

    [Test]
    public async Task An_empty_scenario_with_neither_init_nor_clean_is_rejected()
    {
        var result = ScenarioFactory.CreateScenario(Scenario.Empty("my_empty_scenario"));

        await Assert.That(result.Error).IsTypeOf<ScenarioError.EmptyScenarioWithEmptyInitAndClean>();
    }
}

[NotInParallel]
public class ScenarioConfigValidationTests
{
    [Test]
    public async Task Duplicate_scenario_names_in_the_json_config_are_rejected()
    {
        var context = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("1", _ => Task.FromResult<IResponse>(Response.Ok()))
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(2))))
            .WithoutReports()
            .LoadConfig("Assets/Configuration/duplicate_scenarios_config.json");

        var error = Assert.Throws<AutobahnException>(() => context.Run());

        await Assert.That(error!.Message).Contains("Scenario names are not unique in JSON config");
    }
}
