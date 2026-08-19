using Autobahn.Internal;
using Autobahn.Internal.Domain.Thresholds;
using Autobahn.Metrics;
using Autobahn.Stats;
using Autobahn.Thresholds;
using static Autobahn.Thresholds.ThresholdComparison;
using static Autobahn.Thresholds.ThresholdSubject;

namespace Autobahn.Tests;

internal class ThresholdComparisonTests
{
    [Test]
    [Arguments(LessThan, 10.0, 9.0, true)]
    [Arguments(LessThan, 10.0, 10.0, false)]
    [Arguments(LessThanOrEqual, 10.0, 10.0, true)]
    [Arguments(GreaterThan, 10.0, 11.0, true)]
    [Arguments(GreaterThan, 10.0, 10.0, false)]
    [Arguments(GreaterThanOrEqual, 10.0, 10.0, true)]
    public async Task Every_comparison_means_what_it_says(
        ThresholdComparison comparison, double target, double observed, bool satisfied)
    {
        var threshold = Threshold.Stat(Rps, comparison, target);

        await Assert.That(threshold.IsSatisfiedBy(observed)).IsEqualTo(satisfied);
    }

    [Test]
    public async Task A_threshold_describes_itself_for_the_reports()
    {
        var threshold = Threshold.LatencyBelow(Percent99, 500).ForScenario("checkout").ForStep("pay");

        await Assert.That(threshold.Describe()).IsEqualTo("checkout.pay Percent99 < 500");
    }

    [Test]
    public async Task A_named_threshold_keeps_its_name()
    {
        var threshold = Threshold.ErrorRateBelow(0.01).Named("checkout stays reliable");

        await Assert.That(threshold.Describe()).IsEqualTo("checkout stays reliable");
    }
}

internal class ThresholdValidationTests
{
    private static readonly string[] Scenarios = ["checkout", "browse"];

    private static AppError? Check(Threshold threshold) =>
        ThresholdValidation.Check([threshold], Scenarios) is { IsError: true } r ? r.Error : null;

    [Test]
    public async Task A_rule_about_a_scenario_the_run_does_not_have_fails_up_front()
    {
        var error = Check(Threshold.ErrorRateBelow(0.01).ForScenario("chekcout"));

        await Assert.That(error).IsTypeOf<ThresholdError.UnknownScenario>();
        await Assert.That(error!.Message).Contains("chekcout");
        await Assert.That(error.Message).Contains("checkout");
    }

    [Test]
    public async Task A_subject_that_means_nothing_for_its_scope_is_rejected()
    {
        var error = Check(Threshold.Metric("cache.miss", Percent99, LessThan, 10));

        await Assert.That(error).IsTypeOf<ThresholdError.SubjectDoesNotApply>();
    }

    [Test]
    public async Task A_step_rule_has_to_name_a_step()
    {
        var error = Check(Threshold.Stat(Percent99, LessThan, 10) with { Scope = ThresholdScope.Step });

        await Assert.That(error).IsTypeOf<ThresholdError.MissingTarget>();
        await Assert.That(error!.Message).Contains("step");
    }

    [Test]
    [Arguments(1.5)]
    [Arguments(-0.1)]
    [Arguments(12.0)]
    public async Task A_rate_compared_against_something_that_is_not_a_rate_is_rejected(double value)
    {
        var error = Check(Threshold.ErrorRate(LessThan, value));

        await Assert.That(error).IsTypeOf<ThresholdError.ImpossibleRate>();
    }

    [Test]
    public async Task An_abort_policy_has_to_be_able_to_fire()
    {
        var error = Check(Threshold.ErrorRateBelow(0.01).AbortingAfter(0));

        await Assert.That(error).IsTypeOf<ThresholdError.InvalidAbortAfter>();
    }

    [Test]
    public async Task A_well_formed_set_of_rules_passes()
    {
        var result = ThresholdValidation.Check(
        [
            Threshold.ErrorRateBelow(0.01),
            Threshold.LatencyBelow(Percent99, 500).ForScenario("checkout").ForStep("pay"),
            Threshold.Status("500", StatusCodeCount, LessThan, 10).ForScenario("browse"),
            Threshold.Metric("cache.miss", MetricCurrent, LessThan, 100)
        ], Scenarios);

        await Assert.That(result.IsOk).IsTrue();
    }
}

internal class ThresholdCheckerTests
{
    private static ScenarioStats Stats(
        string name, int ok, int fail, double p99 = 0, params StatusCodeStats[] statusCodes) =>
        new()
        {
            ScenarioName = name,
            Ok = MeasurementStats.Empty with
            {
                Request = new RequestStats { Count = ok, RPS = ok },
                Latency = LatencyStats.Empty with { Percent99 = p99 },
                StatusCodes = statusCodes
            },
            Fail = MeasurementStats.Empty with { Request = new RequestStats { Count = fail, RPS = fail } },
            StepStats = [],
            LoadSimulationStats = new LoadSimulationStats { SimulationName = "inject", Value = 1 },
            CurrentOperation = OperationType.Bombing,
            AllRequestCount = ok + fail,
            AllOkCount = ok,
            AllFailCount = fail,
            AllBytes = 0,
            Duration = TimeSpan.FromSeconds(5)
        };

    [Test]
    public async Task A_rule_that_names_no_scenario_is_tallied_for_each_of_them_separately()
    {
        var checker = new ThresholdChecker([Threshold.ErrorRateBelow(0.1)], ["a", "b"]);

        checker.Check(Time.Seconds(5), [Stats("a", ok: 100, fail: 0), Stats("b", ok: 50, fail: 50)], []);

        var results = checker.GetResults();

        await Assert.That(results.Length).IsEqualTo(2);
        await Assert.That(results.Single(x => x.ScenarioName == "a").Passed).IsTrue();
        await Assert.That(results.Single(x => x.ScenarioName == "b").Passed).IsFalse();
    }

    [Test]
    public async Task A_delayed_rule_is_not_checked_before_its_start_time()
    {
        var checker = new ThresholdChecker(
            [Threshold.ErrorRateBelow(0.1).StartingAfter(Time.Seconds(30))], ["a"]);

        checker.Check(Time.Seconds(5), [Stats("a", ok: 0, fail: 100)], []);

        var duringRamp = checker.GetResults().Single();
        await Assert.That(duringRamp.TotalChecks).IsEqualTo(0);
        await Assert.That(duringRamp.Passed).IsTrue();

        checker.Check(Time.Seconds(35), [Stats("a", ok: 0, fail: 100)], []);

        var afterRamp = checker.GetResults().Single();
        await Assert.That(afterRamp.TotalChecks).IsEqualTo(1);
        await Assert.That(afterRamp.Passed).IsFalse();
        await Assert.That(afterRamp.FirstFailedAt).IsEqualTo(Time.Seconds(35));
    }

    [Test]
    public async Task An_abort_policy_fires_only_after_the_failures_are_consecutive()
    {
        var checker = new ThresholdChecker([Threshold.ErrorRateBelow(0.1).AbortingAfter(3)], ["a"]);

        var bad = Stats("a", ok: 0, fail: 10);
        var good = Stats("a", ok: 10, fail: 0);

        await Assert.That(checker.Check(Time.Seconds(5), [bad], []).ShouldAbort).IsFalse();
        await Assert.That(checker.Check(Time.Seconds(10), [bad], []).ShouldAbort).IsFalse();

        // A good interval resets the streak, so the next two bad ones are not enough either.
        await Assert.That(checker.Check(Time.Seconds(15), [good], []).ShouldAbort).IsFalse();
        await Assert.That(checker.Check(Time.Seconds(20), [bad], []).ShouldAbort).IsFalse();
        await Assert.That(checker.Check(Time.Seconds(25), [bad], []).ShouldAbort).IsFalse();

        var abort = checker.Check(Time.Seconds(30), [bad], []);

        await Assert.That(abort.ShouldAbort).IsTrue();
        await Assert.That(abort.AbortReasons.Single()).Contains("3 checks in a row");
        await Assert.That(checker.GetResults().Single().Aborted).IsTrue();
    }

    [Test]
    public async Task An_advisory_rule_records_the_failure_and_lets_the_run_carry_on()
    {
        var checker = new ThresholdChecker([Threshold.ErrorRateBelow(0.1)], ["a"]);
        var bad = Stats("a", ok: 0, fail: 10);

        for (var i = 1; i <= 5; i++)
            await Assert.That(checker.Check(Time.Seconds(5 * i), [bad], []).ShouldAbort).IsFalse();

        var result = checker.GetResults().Single();

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.Aborted).IsFalse();
        await Assert.That(result.FailedChecks).IsEqualTo(5);
        await Assert.That(result.TotalChecks).IsEqualTo(5);
    }

    [Test]
    public async Task A_window_with_no_requests_has_no_error_rate_to_pass_on()
    {
        var checker = new ThresholdChecker([Threshold.ErrorRateBelow(0.01)], ["a"]);

        checker.Check(Time.Seconds(5), [Stats("a", ok: 0, fail: 0)], []);

        // Reporting 0% for a scenario that did nothing would let a reliability rule pass on
        // a scenario that never ran; the check is skipped instead.
        await Assert.That(checker.GetResults().Single().TotalChecks).IsEqualTo(0);
    }

    [Test]
    public async Task A_status_code_rule_counts_a_code_that_never_came_back_as_zero()
    {
        var checker = new ThresholdChecker([Threshold.Status("500", StatusCodeCount, LessThan, 10)], ["a"]);

        checker.Check(Time.Seconds(5), [Stats("a", ok: 100, fail: 0)], []);

        var result = checker.GetResults().Single();

        await Assert.That(result.TotalChecks).IsEqualTo(1);
        await Assert.That(result.Passed).IsTrue();
        await Assert.That(result.ObservedValue).IsEqualTo(0);
    }

    [Test]
    public async Task A_status_code_rule_reads_the_code_it_names()
    {
        var codes = new[]
        {
            new StatusCodeStats { StatusCode = "500", IsError = true, Message = "", Count = 25 },
            new StatusCodeStats { StatusCode = "200", IsError = false, Message = "", Count = 75 }
        };

        var checker = new ThresholdChecker(
        [
            Threshold.Status("500", StatusCodeCount, LessThan, 10),
            Threshold.Status("500", StatusCodeRate, LessThan, 0.5)
        ], ["a"]);

        checker.Check(Time.Seconds(5), [Stats("a", ok: 100, fail: 0, statusCodes: codes)], []);

        var byCount = checker.GetResults().Single(x => x.Subject == StatusCodeCount);
        var byRate = checker.GetResults().Single(x => x.Subject == StatusCodeRate);

        await Assert.That(byCount.ObservedValue).IsEqualTo(25);
        await Assert.That(byCount.Passed).IsFalse();
        await Assert.That(byRate.ObservedValue).IsEqualTo(0.25);
        await Assert.That(byRate.Passed).IsTrue();
    }

    [Test]
    public async Task A_metric_rule_reads_the_metric_it_names()
    {
        var metrics = new[]
        {
            MetricStats.Empty("cache.miss", MetricKind.Counter, "count") with { Current = 400 },
            MetricStats.Empty("cache.hit", MetricKind.Counter, "count") with { Current = 600 }
        };

        var checker = new ThresholdChecker([Threshold.Metric("cache.miss", MetricCurrent, LessThan, 100)], ["a"]);

        checker.Check(Time.Seconds(5), [Stats("a", ok: 1000, fail: 0)], metrics);

        var result = checker.GetResults().Single();

        await Assert.That(result.ObservedValue).IsEqualTo(400);
        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.ScenarioName).IsEmpty();
    }

    [Test]
    public async Task A_rule_about_something_the_run_never_produced_is_skipped_rather_than_failed()
    {
        var checker = new ThresholdChecker([Threshold.Metric("never.written", MetricCurrent, LessThan, 1)], ["a"]);

        checker.Check(Time.Seconds(5), [Stats("a", ok: 10, fail: 0)], []);

        var result = checker.GetResults().Single();

        await Assert.That(result.TotalChecks).IsEqualTo(0);
        await Assert.That(result.Passed).IsTrue();
    }

    [Test]
    public async Task Results_come_back_ordered_by_name()
    {
        var checker = new ThresholdChecker(
        [
            Threshold.RpsAbove(10).Named("zulu"),
            Threshold.ErrorRateBelow(0.1).Named("alpha"),
            Threshold.LatencyBelow(Percent99, 100).Named("mike")
        ], ["a"]);

        checker.Check(Time.Seconds(5), [Stats("a", ok: 100, fail: 0)], []);

        await Assert.That(checker.GetResults().Select(x => x.Name))
            .IsEquivalentTo(new[] { "alpha", "mike", "zulu" });
    }
}

[NotInParallel]
internal class ThresholdsInARunTests
{
    private static ScenarioProps Flaky(string name, int failEvery) =>
        Scenario.Create(name, async ctx =>
            {
                await Task.Delay(Time.Milliseconds(10), ctx.CancellationToken);

                return ctx.InvocationNumber % failEvery == 0
                    ? Response.Fail(statusCode: "500", message: "boom")
                    : Response.Ok(statusCode: "200");
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.IterationsForConstant(copies: 4, iterations: 200));

    /// <summary>
    /// The exit code is process-wide, so a test that lets a threshold set it would fail the
    /// whole test run. Every test here either opts out or puts it back.
    /// </summary>
    private static void ResetExitCode() => Environment.ExitCode = 0;

    [Test]
    public async Task A_run_that_meets_its_thresholds_says_so()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(Flaky("solid", failEvery: 1_000_000))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .WithThresholds(
                Threshold.ErrorRateBelow(0.01),
                Threshold.Stat(OkCount, GreaterThanOrEqual, 200))
            .Run();

        await Assert.That(stats.AllThresholdsPassed).IsTrue();
        await Assert.That(stats.Thresholds.Length).IsEqualTo(2);
        await Assert.That(stats.Thresholds.All(x => x.TotalChecks > 0)).IsTrue();
        await Assert.That(Environment.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task A_run_that_misses_a_threshold_fails_and_sets_the_exit_code()
    {
        try
        {
            var stats = AutobahnRunner
                .RegisterScenarios(Flaky("flaky", failEvery: 2))
                .WithoutReports()
                .WithoutRuntimeMetrics()
                .WithThresholds(Threshold.ErrorRateBelow(0.01))
                .Run();

            var result = stats.Thresholds.Single();

            await Assert.That(stats.AllThresholdsPassed).IsFalse();
            await Assert.That(result.Passed).IsFalse();
            await Assert.That(result.ObservedValue).IsGreaterThan(0.4);
            await Assert.That(result.FirstFailedAt).IsNotNull();
            await Assert.That(Environment.ExitCode).IsEqualTo(Constants.ThresholdFailedExitCode);
        }
        finally
        {
            ResetExitCode();
        }
    }

    [Test]
    public async Task Opting_out_of_the_exit_code_still_fails_the_run_result()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(Flaky("flaky", failEvery: 2))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .WithThresholds(Threshold.ErrorRateBelow(0.01))
            .WithoutThresholdExitCode()
            .Run();

        await Assert.That(stats.AllThresholdsPassed).IsFalse();
        await Assert.That(Environment.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task A_run_with_no_thresholds_trivially_passes()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(Flaky("whatever", failEvery: 2))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .Run();

        await Assert.That(stats.Thresholds).IsEmpty();
        await Assert.That(stats.AllThresholdsPassed).IsTrue();
        await Assert.That(Environment.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task A_threshold_about_a_scenario_the_run_does_not_have_fails_before_any_load()
    {
        var ex = Assert.Throws<AutobahnException>(() => AutobahnRunner
            .RegisterScenarios(Flaky("real", failEvery: 1_000_000))
            .WithoutReports()
            .WithThresholds(Threshold.ErrorRateBelow(0.01).ForScenario("imaginary"))
            .Run());

        await Assert.That(ex!.Message).Contains("imaginary");
        await Assert.That(ex.Message).Contains("real");
    }

    [Test]
    [Category("slow")]
    public async Task A_threshold_with_an_abort_policy_ends_the_run_early()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("doomed", async ctx =>
                    {
                        await Task.Delay(Time.Milliseconds(10), ctx.CancellationToken);
                        return Response.Fail(statusCode: "500");
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 4, during: Time.Minutes(5))))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .WithReportingInterval(Time.Seconds(5))
            .WithThresholds(Threshold.ErrorRateBelow(0.5).AbortingAfter(1))
            .WithoutThresholdExitCode()
            .Run();

        var result = stats.Thresholds.Single();

        // The plan asked for five minutes; the rule ended it on the first reporting interval.
        await Assert.That(result.Aborted).IsTrue();
        await Assert.That(result.Passed).IsFalse();
        await Assert.That(stats.Duration).IsLessThan(Time.Minutes(1));
    }

    [Test]
    [Category("slow")]
    public async Task A_delayed_threshold_ignores_the_ramp_it_was_told_to_ignore()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("ramping", async ctx =>
                    {
                        await Task.Delay(Time.Milliseconds(10), ctx.CancellationToken);
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(
                        Simulation.RampingInject(rate: 40, interval: Time.Seconds(1), during: Time.Seconds(6)),
                        Simulation.Inject(rate: 40, interval: Time.Seconds(1), during: Time.Seconds(10))))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .WithReportingInterval(Time.Seconds(5))
            // Throughput during the ramp is well under 30/s; only the steady state clears it.
            .WithThresholds(Threshold.RpsAbove(30).StartingAfter(Time.Seconds(8)))
            .WithoutThresholdExitCode()
            .Run();

        var result = stats.Thresholds.Single();

        await Assert.That(result.TotalChecks).IsGreaterThan(0);
        await Assert.That(result.Passed).IsTrue();
    }

    [Test]
    public async Task A_step_threshold_reads_the_step_it_names()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("two_steps", async ctx =>
                    {
                        await Step.Run("fast", ctx, async () =>
                        {
                            await Task.Delay(Time.Milliseconds(5));
                            return Response.Ok();
                        });

                        return await Step.Run("slow", ctx, async () =>
                        {
                            await Task.Delay(Time.Milliseconds(80));
                            return Response.Ok();
                        });
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.IterationsForConstant(copies: 2, iterations: 40)))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .WithThresholds(
                Threshold.LatencyBelow(MeanLatency, 40).ForStep("fast").Named("fast step is fast"),
                Threshold.LatencyBelow(MeanLatency, 40).ForStep("slow").Named("slow step is fast"))
            .WithoutThresholdExitCode()
            .Run();

        await Assert.That(stats.Thresholds.Single(x => x.Name == "fast step is fast").Passed).IsTrue();
        await Assert.That(stats.Thresholds.Single(x => x.Name == "slow step is fast").Passed).IsFalse();
    }

    [Test]
    public async Task A_metric_threshold_reads_a_metric_the_scenario_wrote()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("counting", async ctx =>
                    {
                        ctx.Metrics.Counter("widgets").Increment();
                        await Task.Delay(Time.Milliseconds(5), ctx.CancellationToken);
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.IterationsForConstant(copies: 2, iterations: 100)))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .WithThresholds(
                Threshold.Metric("widgets", MetricCurrent, GreaterThanOrEqual, 100).Named("enough widgets"),
                Threshold.Metric("widgets", MetricCurrent, LessThan, 10).Named("too many widgets"))
            .WithoutThresholdExitCode()
            .Run();

        await Assert.That(stats.Thresholds.Single(x => x.Name == "enough widgets").Passed).IsTrue();
        await Assert.That(stats.Thresholds.Single(x => x.Name == "too many widgets").Passed).IsFalse();
    }

    [Test]
    [Category("slow")]
    public async Task Thresholds_reach_every_report_format()
    {
        var reportFolder = Path.Combine(Path.GetTempPath(), $"autobahn_thresholds_{Guid.NewGuid():N}");

        var stats = AutobahnRunner
            .RegisterScenarios(Flaky("gated", failEvery: 1_000_000))
            .WithReportFolder(reportFolder)
            .WithoutRuntimeMetrics()
            .WithThresholds(Threshold.ErrorRateBelow(0.01).Named("stays reliable"))
            .Run();

        var files = stats.ReportFiles.ToDictionary(x => Path.GetFileName(x.FilePath), x => x.ReportContent);

        await Assert.That(files.Keys.Any(x => x.EndsWith("_thresholds.csv"))).IsTrue();

        // The step CSV is one row per step and carries no thresholds; they get their own file.
        foreach (var (name, content) in files.Where(x => !x.Key.EndsWith(".csv") || x.Key.EndsWith("_thresholds.csv")))
            await Assert.That(content).Contains("stays reliable").Because($"{name} should carry the threshold");

        Directory.Delete(reportFolder, recursive: true);
    }
}

[NotInParallel]
internal class DeclarativeThresholdTests
{
    private static ScenarioProps Checkout() =>
        Scenario.Create("checkout", async ctx =>
            {
                await Task.Delay(Time.Milliseconds(10), ctx.CancellationToken);
                return Response.Ok(statusCode: "200");
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.IterationsForConstant(copies: 2, iterations: 100));

    [Test]
    public async Task Thresholds_declared_in_the_json_config_gate_the_run()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(Checkout())
            .LoadConfig("Assets/Configuration/thresholds_config.json")
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .Run();

        var names = stats.Thresholds.Select(x => x.Name).ToArray();

        await Assert.That(names).Contains("everything stays reliable");
        await Assert.That(names).Contains("few server errors");

        // A rule under a scenario's settings block takes that scenario's name from the block
        // it sits in, so it does not have to repeat it.
        var fromScenarioBlock = stats.Thresholds.Single(x => x.Name == "checkout p99 under 5s");
        await Assert.That(fromScenarioBlock.ScenarioName).IsEqualTo("checkout");

        await Assert.That(stats.AllThresholdsPassed).IsTrue();
    }

    [Test]
    public async Task Config_thresholds_add_to_the_ones_declared_in_code_rather_than_replacing_them()
    {
        var stats = AutobahnRunner
            .RegisterScenarios(Checkout())
            .LoadConfig("Assets/Configuration/thresholds_config.json")
            .WithThresholds(Threshold.RpsAbove(0).Named("from code"))
            .WithoutReports()
            .WithoutRuntimeMetrics()
            .Run();

        await Assert.That(stats.Thresholds.Length).IsEqualTo(4);
        await Assert.That(stats.Thresholds.Select(x => x.Name)).Contains("from code");
    }
}

internal class FinalOnlyThresholdTests
{
    private static ScenarioStats Stats(int ok) => new()
    {
        ScenarioName = "a",
        Ok = MeasurementStats.Empty with { Request = new RequestStats { Count = ok, RPS = ok } },
        Fail = MeasurementStats.Empty,
        StepStats = [],
        LoadSimulationStats = new LoadSimulationStats { SimulationName = "inject", Value = 1 },
        CurrentOperation = OperationType.Bombing,
        AllRequestCount = ok,
        AllOkCount = ok,
        AllFailCount = 0,
        AllBytes = 0,
        Duration = TimeSpan.FromSeconds(5)
    };

    [Test]
    public async Task A_cumulative_rule_is_checked_once_against_the_whole_run()
    {
        var checker = new ThresholdChecker(
            [Threshold.Stat(OkCount, GreaterThanOrEqual, 1_000).OnlyAtTheEnd()], ["a"]);

        // Each interval saw 300, which would fail the rule five times over if it were checked.
        for (var i = 1; i <= 5; i++) checker.Check(Time.Seconds(5 * i), [Stats(300)], []);

        await Assert.That(checker.GetResults().Single().TotalChecks).IsEqualTo(0);

        checker.Check(Time.Seconds(25), [Stats(1_500)], [], isFinal: true);

        var result = checker.GetResults().Single();

        await Assert.That(result.TotalChecks).IsEqualTo(1);
        await Assert.That(result.Passed).IsTrue();
        await Assert.That(result.ObservedValue).IsEqualTo(1_500);
    }

    [Test]
    public async Task Without_it_the_same_rule_is_checked_every_interval()
    {
        var checker = new ThresholdChecker([Threshold.Stat(OkCount, GreaterThanOrEqual, 1_000)], ["a"]);

        for (var i = 1; i <= 5; i++) checker.Check(Time.Seconds(5 * i), [Stats(300)], []);

        var result = checker.GetResults().Single();

        await Assert.That(result.TotalChecks).IsEqualTo(5);
        await Assert.That(result.FailedChecks).IsEqualTo(5);
    }
}
