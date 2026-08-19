using Autobahn.Configuration;
using Autobahn.Internal;
using Autobahn.Internal.Services;
using Autobahn.Stats;

namespace Autobahn.Tests;

internal class ContextResolverTests
{
    private static readonly GlobalSettings BaseGlobalSettings = GlobalSettings.Empty;

    private static ScenarioProps BaseScenario() =>
        Scenario.Create("1", _ => Task.FromResult<IResponse>(Response.Fail())).WithoutWarmUp();

    private static readonly AutobahnConfig BaseConfig = new()
    {
        TestSuite = Constants.DefaultTestSuite,
        TestName = Constants.DefaultTestName,
        TargetScenarios = ["1"],
        GlobalSettings = null
    };

    private static AutobahnContext BaseContext() =>
        AutobahnRunner.RegisterScenarios(BaseScenario()).EnableHintsAnalyzer(false);

    [Test]
    public async Task Every_registered_scenario_is_a_target_when_none_is_named()
    {
        var context = BaseContext() with { Config = BaseConfig with { TargetScenarios = null } };

        await Assert.That(ContextResolver.GetTargetScenarios(context).Count).IsEqualTo(1);
    }

    [Test]
    public async Task The_config_can_narrow_the_target_scenarios()
    {
        var context = BaseContext() with
        {
            Config = BaseConfig with { TargetScenarios = ["10"] },
            RegisteredScenarios = [BaseScenario() with { ScenarioName = "1" }, BaseScenario() with { ScenarioName = "2" }]
        };

        var targets = ContextResolver.GetTargetScenarios(context);

        await Assert.That(targets.Count).IsEqualTo(1);
        await Assert.That(targets[0]).IsEqualTo("10");
    }

    [Test]
    [Arguments("from_config", "from_code", "from_config")]
    [Arguments("from_config", null, "from_config")]
    [Arguments(null, "from_code", "from_code")]
    public async Task The_report_file_name_prefers_the_config_then_the_code(
        string? configValue, string? contextValue, string expected)
    {
        var context = BaseContext() with
        {
            Config = BaseConfig with { GlobalSettings = BaseGlobalSettings with { ReportFileName = configValue } },
            Reporting = AutobahnContext.Empty.Reporting with { Formats = [ReportFormat.Txt], FileName = contextValue }
        };

        await Assert.That(ContextResolver.GetReportFileNameOrDefault(DateTime.UtcNow, context)).IsEqualTo(expected);
    }

    [Test]
    public async Task With_no_report_file_name_anywhere_the_default_carries_a_timestamp()
    {
        var context = BaseContext() with
        {
            Config = BaseConfig with { GlobalSettings = BaseGlobalSettings },
            Reporting = AutobahnContext.Empty.Reporting with { FileName = null }
        };

        var currentTime = DateTime.UtcNow;
        var fileName = ContextResolver.GetReportFileNameOrDefault(currentTime, context);

        await Assert.That(fileName)
            .IsEqualTo($"{Constants.DefaultReportName}_{currentTime:yyyy-MM-dd--HH-mm-ss}");
    }

    [Test]
    public async Task Report_formats_prefer_the_config_then_the_code()
    {
        var fromConfig = BaseContext() with
        {
            Config = BaseConfig with { GlobalSettings = BaseGlobalSettings with { ReportFormats = [ReportFormat.Md] } },
            Reporting = AutobahnContext.Empty.Reporting with { Formats = [ReportFormat.Txt] }
        };

        var fromCode = BaseContext() with
        {
            Config = BaseConfig with { GlobalSettings = BaseGlobalSettings },
            Reporting = AutobahnContext.Empty.Reporting with { Formats = [ReportFormat.Txt] }
        };

        await Assert.That(ContextResolver.GetReportFormats(fromConfig)).IsEquivalentTo(new[] { ReportFormat.Md });
        await Assert.That(ContextResolver.GetReportFormats(fromCode)).IsEquivalentTo(new[] { ReportFormat.Txt });
    }

    [Test]
    [Arguments(true, false, true)]
    [Arguments(false, true, false)]
    [Arguments(null, true, true)]
    [Arguments(null, false, false)]
    public async Task The_hints_analyzer_setting_prefers_the_config_then_the_code(
        bool? configValue, bool contextValue, bool expected)
    {
        var context = BaseContext() with
        {
            Config = BaseConfig with { GlobalSettings = BaseGlobalSettings with { EnableHintsAnalyzer = configValue } },
            EnableHintsAnalyzer = contextValue
        };

        await Assert.That(ContextResolver.GetEnableHintsAnalyzer(context)).IsEqualTo(expected);
    }

    [Test]
    public async Task Test_suite_and_name_prefer_the_config_then_the_code()
    {
        var context = BaseContext();
        var withConfig = context with { Config = BaseConfig with { TestSuite = "from_config", TestName = "also_config" } };

        await Assert.That(ContextResolver.GetTestSuite(withConfig)).IsEqualTo("from_config");
        await Assert.That(ContextResolver.GetTestName(withConfig)).IsEqualTo("also_config");

        var withoutConfig = context with { Config = null };

        await Assert.That(ContextResolver.GetTestSuite(withoutConfig)).IsEqualTo(context.TestSuite);
        await Assert.That(ContextResolver.GetTestName(withoutConfig)).IsEqualTo(context.TestName);
    }

    [Test]
    public async Task A_reporting_interval_below_the_minimum_is_rejected()
    {
        var okContext = BaseContext() with
        {
            Reporting = AutobahnContext.Empty.Reporting with { ReportingInterval = Time.Seconds(5) }
        };

        var errorContext = BaseContext() with
        {
            Reporting = AutobahnContext.Empty.Reporting with { ReportingInterval = Time.Seconds(2) }
        };

        await Assert.That(ContextResolver.GetReportingInterval(okContext).IsOk).IsTrue();
        await Assert.That(ContextResolver.GetReportingInterval(errorContext).IsError).IsTrue();
        await Assert.That(ContextResolver.CheckReportingInterval(Time.Seconds(3)).Error)
            .IsTypeOf<ReportError.ReportingIntervalSmallerThanMin>();
    }

    [Test]
    public async Task A_target_scenario_that_does_not_exist_is_rejected()
    {
        var scn = BaseScenario() with { ScenarioName = "1" };

        await Assert.That(ContextResolver.CheckAvailableTargets([scn], [" "]).Error)
            .IsTypeOf<ScenarioError.TargetScenariosNotFound>();

        await Assert.That(ContextResolver.CheckAvailableTargets([scn], ["3"]).Error)
            .IsTypeOf<ScenarioError.TargetScenariosNotFound>();
    }

    [Test]
    public async Task An_empty_report_name_is_rejected()
    {
        await Assert.That(ContextResolver.CheckReportName(" ").Error).IsTypeOf<ReportError.EmptyReportName>();
    }

    [Test]
    public async Task A_report_name_with_invalid_characters_is_rejected()
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            var result = ContextResolver.CheckReportName(invalid.ToString());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Error is ReportError.InvalidReportName or ReportError.EmptyReportName).IsTrue();
        }
    }

    [Test]
    public async Task An_empty_report_folder_is_rejected()
    {
        await Assert.That(ContextResolver.CheckReportFolder(" ").Error).IsTypeOf<ReportError.EmptyReportFolderPath>();
    }

    [Test]
    public async Task A_report_folder_with_invalid_characters_is_rejected()
    {
        foreach (var invalid in Path.GetInvalidPathChars())
        {
            var result = ContextResolver.CheckReportFolder(invalid.ToString());

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Error is ReportError.InvalidReportFolderPath or ReportError.EmptyReportFolderPath).IsTrue();
        }
    }

    [Test]
    public async Task Session_args_come_out_fully_resolved_with_no_config_at_all()
    {
        var context = AutobahnRunner.RegisterScenarios(BaseScenario());
        var sessionArgs = ContextResolver.CreateSessionArgs(SessionArgs.Empty.TestInfo, context);

        await Assert.That(context.Config).IsNull();
        await Assert.That(sessionArgs.IsOk).IsTrue();

        var args = sessionArgs.Value;

        await Assert.That(args.ReportFileName).IsNotEmpty();
        await Assert.That(args.ReportFolder).IsNotEmpty();
        await Assert.That(args.ReportFormats).IsNotEmpty();
        await Assert.That(args.ReportingInterval).IsEqualTo(Constants.DefaultReportingInterval);
        await Assert.That(args.TargetScenarios).IsEquivalentTo(new[] { "1" });
        await Assert.That(args.ScenariosSettings).IsEmpty();
        await Assert.That(args.EnableHintsAnalyzer).IsFalse();
        await Assert.That(args.EnableStopTestForcibly).IsFalse();
    }

    [Test]
    public async Task Running_with_no_registered_scenarios_says_so()
    {
        var error = Assert.Throws<AutobahnException>(() => AutobahnRunner.RegisterScenarios().Run());

        await Assert.That(error!.Message).Contains("No scenarios were registered");
    }
}
