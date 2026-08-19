using Autobahn.Internal;

namespace Autobahn.Tests;

[NotInParallel]
public class EmptyScenarioTests
{
    [Test]
    public async Task An_empty_scenario_with_neither_init_nor_clean_stops_the_run()
    {
        var context = AutobahnRunner.RegisterScenarios(Scenario.Empty("my_empty_scenario")).WithoutReports();

        var error = Assert.Throws<AutobahnException>(() => context.RunWithResult());

        await Assert.That(error!.Error).IsTypeOf<ScenarioError.EmptyScenarioWithEmptyInitAndClean>();
    }

    [Test]
    public async Task An_empty_scenario_with_only_a_clean_function_runs()
    {
        var cleanInvoked = false;

        AutobahnRunner
            .RegisterScenarios(Scenario.Empty("my_empty_scenario").WithClean(_ =>
            {
                cleanInvoked = true;
                return Task.CompletedTask;
            }))
            .WithoutReports()
            .Run();

        await Assert.That(cleanInvoked).IsTrue();
    }

    [Test]
    public async Task An_empty_scenario_with_only_an_init_function_runs()
    {
        var initInvoked = false;

        AutobahnRunner
            .RegisterScenarios(Scenario.Empty("my_empty_scenario").WithInit(_ =>
            {
                initInvoked = true;
                return Task.CompletedTask;
            }))
            .WithoutReports()
            .Run();

        await Assert.That(initInvoked).IsTrue();
    }

    [Test]
    public async Task An_empty_scenario_produces_no_statistics_of_its_own()
    {
        var initInvoked = false;

        var scn1 = Scenario.Create("scenario_1", async _ =>
            {
                await Task.Delay(Time.Milliseconds(10));
                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(1)));

        var emptyScn = Scenario.Empty("my_empty_scenario").WithInit(_ =>
        {
            initInvoked = true;
            return Task.CompletedTask;
        });

        var stats = AutobahnRunner.RegisterScenarios(scn1, emptyScn).WithoutReports().Run();

        await Assert.That(initInvoked).IsTrue();
        await Assert.That(stats.ScenarioStats.Length).IsEqualTo(1);
        await Assert.That(stats.ScenarioStats[0].ScenarioName).IsEqualTo("scenario_1");
    }
}
