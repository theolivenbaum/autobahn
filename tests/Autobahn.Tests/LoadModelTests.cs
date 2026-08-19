using Autobahn.Internal;
using Autobahn.Internal.Domain;

namespace Autobahn.Tests;

internal class LoadPlanValidationTests
{
    private static ScenarioProps Scn(params LoadSimulation[] simulations) =>
        Scenario.Create("checkout", _ => Task.FromResult<IResponse>(Response.Ok()))
            .WithoutWarmUp()
            .WithLoadSimulations(simulations);

    [Test]
    public async Task A_scenario_with_no_load_simulations_is_rejected_by_name()
    {
        var result = ScenarioFactory.CreateScenario(Scn());

        await Assert.That(result.Error).IsTypeOf<LoadSimulationError.EmptySimulationsList>();
        await Assert.That(result.Error.Message).Contains("checkout");
    }

    [Test]
    public async Task A_random_injection_whose_bounds_do_not_straddle_anything_is_rejected()
    {
        var result = ScenarioFactory.CreateScenario(
            Scn(Simulation.InjectRandom(minRate: 10, maxRate: 10, Time.Seconds(1), Time.Seconds(10))));

        await Assert.That(result.Error).IsTypeOf<LoadSimulationError.RandomRatesAreNotAscending>();
        await Assert.That(result.Error.Message).Contains("checkout");
        await Assert.That(result.Error.Message).Contains("minRate");
    }

    [Test]
    public async Task An_injection_interval_of_zero_is_rejected()
    {
        var result = ScenarioFactory.CreateScenario(Scn(Simulation.Inject(10, TimeSpan.Zero, Time.Seconds(10))));

        await Assert.That(result.Error).IsTypeOf<LoadSimulationError.IntervalIsNotPositive>();
    }

    [Test]
    public async Task A_counted_simulation_needs_a_positive_iteration_count()
    {
        var result = ScenarioFactory.CreateScenario(Scn(Simulation.IterationsForConstant(1, 0)));

        await Assert.That(result.Error).IsTypeOf<LoadSimulationError.IterationsCountIsNotPositive>();
    }

    [Test]
    [MethodDataSource(typeof(LoadPlanValidationTests), nameof(InvalidPlans))]
    public async Task Every_load_plan_error_names_the_scenario_it_came_from(LoadSimulation simulation)
    {
        var result = ScenarioFactory.CreateScenario(Scn(simulation));

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Error.Message).Contains("checkout");
    }

    public static IEnumerable<Func<LoadSimulation>> InvalidPlans() =>
    [
        () => Simulation.KeepConstant(-1, Time.Seconds(10)),
        () => Simulation.KeepConstant(1, TimeSpan.FromMilliseconds(1)),
        () => Simulation.Inject(-1, Time.Seconds(1), Time.Seconds(10)),
        () => Simulation.Inject(1, Time.Seconds(30), Time.Seconds(10)),
        () => Simulation.InjectRandom(5, 1, Time.Seconds(1), Time.Seconds(10)),
        () => Simulation.IterationsForConstant(1, -1),
        () => Simulation.IterationsForInject(1, TimeSpan.Zero, 10)
    ];
}

internal class ScenarioWeightTests
{
    private static ScenarioProps Scn(string name, int? weight, int rate) =>
        Scenario.Create(name, _ => Task.FromResult<IResponse>(Response.Ok()))
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.Inject(rate, Time.Seconds(1), Time.Seconds(10)))
            .WithWeight(weight ?? 0) with { Weight = weight };

    [Test]
    public async Task Weights_split_the_combined_load_between_scenarios()
    {
        var scenarios = ScenarioFactory.CreateScenarios(
        [
            Scn("read", weight: 80, rate: 100),
            Scn("write", weight: 20, rate: 100)
        ]);

        await Assert.That(scenarios.IsOk).IsTrue();

        var read = (LoadSimulation.Inject)scenarios.Value.Single(x => x.ScenarioName == "read").LoadSimulations[0].Value;
        var write = (LoadSimulation.Inject)scenarios.Value.Single(x => x.ScenarioName == "write").LoadSimulations[0].Value;

        await Assert.That(read.Rate).IsEqualTo(80);
        await Assert.That(write.Rate).IsEqualTo(20);
    }

    [Test]
    public async Task Weights_are_relative_so_the_same_ratio_gives_the_same_split()
    {
        var scenarios = ScenarioFactory.CreateScenarios(
        [
            Scn("read", weight: 8, rate: 100),
            Scn("write", weight: 2, rate: 100)
        ]).Value;

        var read = (LoadSimulation.Inject)scenarios.Single(x => x.ScenarioName == "read").LoadSimulations[0].Value;

        await Assert.That(read.Rate).IsEqualTo(80);
    }

    [Test]
    public async Task A_scenario_whose_share_rounds_to_nothing_still_runs_one_copy()
    {
        var scenarios = ScenarioFactory.CreateScenarios(
        [
            Scn("read", weight: 9_999, rate: 10),
            Scn("write", weight: 1, rate: 10)
        ]).Value;

        var write = (LoadSimulation.Inject)scenarios.Single(x => x.ScenarioName == "write").LoadSimulations[0].Value;

        await Assert.That(write.Rate).IsEqualTo(1);
    }

    [Test]
    public async Task An_unweighted_run_carries_its_plans_as_written()
    {
        var scenarios = ScenarioFactory.CreateScenarios(
        [
            Scn("read", weight: null, rate: 100),
            Scn("write", weight: null, rate: 100)
        ]).Value;

        foreach (var scenario in scenarios)
        {
            var inject = (LoadSimulation.Inject)scenario.LoadSimulations[0].Value;
            await Assert.That(inject.Rate).IsEqualTo(100);
        }
    }

    [Test]
    public async Task Weighting_only_some_of_the_scenarios_is_rejected()
    {
        var result = ScenarioFactory.CreateScenarios(
        [
            Scn("read", weight: 80, rate: 100),
            Scn("write", weight: null, rate: 100)
        ]);

        await Assert.That(result.Error).IsTypeOf<ScenarioError.MixedScenarioWeights>();
        await Assert.That(result.Error.Message).Contains("read");
        await Assert.That(result.Error.Message).Contains("write");
    }

    [Test]
    public async Task A_weight_of_zero_or_less_is_rejected()
    {
        var result = ScenarioFactory.CreateScenarios([Scn("read", weight: 0, rate: 100)]);

        await Assert.That(result.Error).IsTypeOf<ScenarioError.InvalidScenarioWeight>();
    }

    [Test]
    public async Task A_ramp_is_scaled_at_both_ends_so_it_stays_correct_while_it_climbs()
    {
        var props = Scenario.Create("read", _ => Task.FromResult<IResponse>(Response.Ok()))
            .WithoutWarmUp()
            .WithLoadSimulations(
                Simulation.RampingConstant(100, Time.Seconds(10)),
                Simulation.KeepConstant(100, Time.Seconds(10))) with { Weight = 25 };

        var other = Scenario.Create("write", _ => Task.FromResult<IResponse>(Response.Ok()))
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(100, Time.Seconds(20))) with { Weight = 75 };

        var scenarios = ScenarioFactory.CreateScenarios([props, other]).Value;
        var read = scenarios.Single(x => x.ScenarioName == "read");

        // The ramp climbs to the scaled target, and the hold that follows starts from it.
        await Assert.That(((LoadSimulation.RampingConstant)read.LoadSimulations[0].Value).Copies).IsEqualTo(25);
        await Assert.That(((LoadSimulation.KeepConstant)read.LoadSimulations[1].Value).Copies).IsEqualTo(25);
        await Assert.That(read.LoadSimulations[1].PrevActorCount).IsEqualTo(25);
    }
}

internal class MaxCopiesCountTests
{
    [Test]
    public async Task The_copy_count_is_the_highest_the_plan_ever_reaches()
    {
        var scenario = ScenarioFactory.CreateScenario(
            Scenario.Create("s", _ => Task.FromResult<IResponse>(Response.Ok()))
                .WithoutWarmUp()
                .WithLoadSimulations(
                    Simulation.RampingConstant(10, Time.Seconds(10)),
                    Simulation.KeepConstant(50, Time.Seconds(10)),
                    Simulation.Inject(30, Time.Seconds(1), Time.Seconds(10)))).Value;

        await Assert.That(scenario.MaxCopiesCount).IsEqualTo(50);
    }
}

public class DistributionTests
{
    [Test]
    public async Task A_uniform_draw_touches_every_item()
    {
        var distribution = Distribution.Uniform<string>(["a", "b", "c"]);
        var seen = new HashSet<string>();

        for (var i = 0; i < 500; i++) seen.Add(distribution.Next());

        await Assert.That(seen.Count).IsEqualTo(3);
    }

    [Test]
    public async Task A_zipfian_draw_favours_the_head_of_the_list()
    {
        var distribution = Distribution.Zipfian<int>([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var counts = new int[10];

        for (var i = 0; i < 20_000; i++) counts[distribution.Next()]++;

        // Classic Zipf: item 0 is drawn about twice as often as item 1, and the order is
        // monotonic. Asserted as a comfortable ratio rather than an exact one, because it is
        // a random draw.
        await Assert.That(counts[0]).IsGreaterThan(counts[1]);
        await Assert.That(counts[1]).IsGreaterThan(counts[9]);
        await Assert.That(counts[0]).IsGreaterThan(counts[9] * 3);
        await Assert.That(counts[9]).IsGreaterThan(0);
    }

    [Test]
    public async Task A_higher_skew_concentrates_the_draw_further_on_the_head()
    {
        var gentle = Distribution.Zipfian<int>([0, 1, 2, 3, 4], skew: 0.5);
        var steep = Distribution.Zipfian<int>([0, 1, 2, 3, 4], skew: 2.0);

        var gentleHead = 0;
        var steepHead = 0;

        for (var i = 0; i < 20_000; i++)
        {
            if (gentle.Next() == 0) gentleHead++;
            if (steep.Next() == 0) steepHead++;
        }

        await Assert.That(steepHead).IsGreaterThan(gentleHead);
    }

    [Test]
    public async Task A_multinomial_draw_follows_its_weights()
    {
        var distribution = Distribution.Multinomial(("read", 80.0), ("write", 15.0), ("delete", 5.0));
        var counts = new Dictionary<string, int> { ["read"] = 0, ["write"] = 0, ["delete"] = 0 };

        const int draws = 40_000;
        for (var i = 0; i < draws; i++) counts[distribution.Next()]++;

        await Assert.That(counts["read"] / (double)draws).IsBetween(0.76, 0.84);
        await Assert.That(counts["write"] / (double)draws).IsBetween(0.12, 0.18);
        await Assert.That(counts["delete"] / (double)draws).IsBetween(0.03, 0.07);
    }

    [Test]
    public async Task A_choice_with_no_weight_is_never_drawn()
    {
        var distribution = Distribution.Multinomial(("used", 1.0), ("never", 0.0));

        for (var i = 0; i < 2_000; i++)
            await Assert.That(distribution.Next()).IsEqualTo("used");
    }

    [Test]
    public async Task An_empty_distribution_is_rejected()
    {
        await Assert.That(() => Distribution.Uniform<string>([])).Throws<ArgumentException>();
        await Assert.That(() => Distribution.Zipfian<string>([])).Throws<ArgumentException>();
        await Assert.That(() => Distribution.Multinomial<string>()).Throws<ArgumentException>();
    }

    [Test]
    public async Task Weights_that_are_all_zero_are_rejected()
    {
        await Assert.That(() => Distribution.Multinomial(("a", 0.0), ("b", 0.0))).Throws<ArgumentException>();
    }
}
