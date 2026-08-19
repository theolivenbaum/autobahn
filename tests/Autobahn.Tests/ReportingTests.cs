using Microsoft.Extensions.Logging;
using Autobahn.Stats;

namespace Autobahn.Tests;

[NotInParallel]
public class ReportingTests
{
    private static ScenarioProps ShortScenario(string name, params string[] steps)
    {
        return Scenario.Create(name, async ctx =>
            {
                foreach (var step in steps)
                {
                    await Step.Run(step, ctx, async () =>
                    {
                        await Task.Delay(Time.Milliseconds(100));
                        return Response.Ok(statusCode: "200", sizeBytes: 128);
                    });
                }

                return Response.Ok(statusCode: "200");
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(copies: 2, during: Time.Seconds(5)));
    }

    private static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    [Test]
    public async Task Every_report_format_is_written_and_carries_content()
    {
        const string folder = "./reports-all-formats";
        DeleteFolder(folder);

        var stats = AutobahnRunner
            .RegisterScenarios(ShortScenario("test", "ok step"))
            .WithReportFileName("custom_report_name")
            .WithReportFolder(folder)
            .Run();

        var files = Directory.GetFiles(folder, "*.*", SearchOption.TopDirectoryOnly).Select(x => new FileInfo(x)).ToArray();

        // Five report formats, the metrics and threshold CSVs that ride alongside the step
        // one, and the run's own log file. The threshold CSV is absent: this run declared none.
        await Assert.That(files.Length).IsEqualTo(7);
        await Assert.That(files.Count(x => x.Name.EndsWith("_metrics.csv"))).IsEqualTo(1);
        await Assert.That(files.Count(x => x.Name.EndsWith("_thresholds.csv"))).IsEqualTo(0);

        foreach (var file in files)
        {
            await Assert.That(file.Name.Contains("custom_report_name") || file.Name.Contains(Constants.LogFilePrefix))
                .IsTrue();

            await Assert.That(new[] { ".html", ".csv", ".txt", ".md", ".json" }).Contains(file.Extension);
            await Assert.That(file.Length).IsGreaterThan(0L);
        }

        await Assert.That(stats.ReportFiles.Length).IsEqualTo(6);

        // Every format renders; none of them falls back to the "could not generate" text.
        foreach (var report in stats.ReportFiles)
        {
            await Assert.That(report.ReportContent).IsNotEmpty();
            await Assert.That(report.ReportContent).DoesNotContain("Could not generate report");
        }
    }

    [Test]
    public async Task A_report_file_holds_exactly_what_the_stats_say_it_holds()
    {
        const string folder = "./reports-content-match";
        DeleteFolder(folder);

        var stats = AutobahnRunner
            .RegisterScenarios(ShortScenario("test", "ok step"))
            .WithReportFolder(folder)
            .Run();

        foreach (var reportFile in stats.ReportFiles)
        {
            var fileContent = await File.ReadAllTextAsync(reportFile.FilePath);
            await Assert.That(reportFile.ReportContent).IsEqualTo(fileContent);
        }
    }

    [Test]
    public async Task The_csv_report_has_one_row_per_step_and_a_stable_column_count()
    {
        const string folder = "./reports-csv";
        DeleteFolder(folder);

        AutobahnRunner
            .RegisterScenarios(
                ShortScenario("test1", "ok step 1"),
                ShortScenario("test2", "ok step 2", "ok step 3"))
            .WithReportFolder(folder)
            .WithReportFormats(ReportFormat.Csv)
            .Run();

        // The metrics ride in their own CSV beside this one; this test is about the step rows.
        var csvFile = Directory.GetFiles(folder)
            .Select(x => new FileInfo(x))
            .First(x => x.Extension == ".csv" && !x.Name.EndsWith("_metrics.csv"));
        var csvRows = await File.ReadAllLinesAsync(csvFile.FullName);

        // header + (scenario + 1 step) + (scenario + 2 steps)
        await Assert.That(csvRows.Length).IsEqualTo(6);

        var columnCounts = csvRows.Select(row => row.Split(',').Length).Distinct().ToArray();
        await Assert.That(columnCounts.Length).IsEqualTo(1);
    }

    [Test]
    public async Task The_json_config_decides_the_report_name_folder_and_formats()
    {
        const string folder = "./my_custom_reports";
        DeleteFolder(folder);

        AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("test", async _ =>
                    {
                        await Task.Delay(Time.Seconds(1));
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 5, during: Time.Seconds(5))))
            .LoadConfig("Assets/Configuration/test_config_2.json")
            .Run();

        var files = Directory.GetFiles(folder, "*.*", SearchOption.TopDirectoryOnly).Select(x => new FileInfo(x)).ToArray();

        // "ReportFormats": ["Html", "Txt"] plus the log file.
        await Assert.That(files.Length).IsEqualTo(3);

        foreach (var file in files)
        {
            await Assert.That(file.Name.Contains("custom_report_name") || file.Name.Contains(Constants.LogFilePrefix))
                .IsTrue();

            await Assert.That(new[] { ".html", ".txt" }).Contains(file.Extension);
            await Assert.That(file.Length).IsGreaterThan(0L);
        }
    }

    [Test]
    public async Task WithoutReports_builds_the_console_summary_but_writes_no_files()
    {
        const string folder = "./no-reports";
        DeleteFolder(folder);

        var logs = new InMemoryLoggerProvider();

        var stats = AutobahnRunner
            .RegisterScenarios(
                Scenario.Create("test", async _ =>
                    {
                        await Task.Delay(Time.Seconds(1));
                        return Response.Ok();
                    })
                    .WithoutWarmUp()
                    .WithLoadSimulations(Simulation.KeepConstant(copies: 5, during: Time.Seconds(5))))
            .WithoutReports()
            .WithReportFolder(folder)
            .WithLogging(builder => builder.AddProvider(logs))
            .WithMinimumLogLevel(LogLevel.Trace)
            .Run("disposeLogger=false");

        await Assert.That(stats.ReportFiles).IsEmpty();

        // The reports are built lazily, so only the console one is ever rendered.
        await Assert.That(logs.HasMessage("Report.build")).IsTrue();
        await Assert.That(logs.HasMessage("ConsoleReport.print")).IsTrue();
        await Assert.That(logs.HasMessage("TxtReport.print")).IsFalse();
        await Assert.That(logs.HasMessage("CsvReport.print")).IsFalse();
        await Assert.That(logs.HasMessage("HtmlReport.print")).IsFalse();
        await Assert.That(logs.HasMessage("MdReport.print")).IsFalse();

        // Logging was taken over by the test's own provider, so this run writes nothing at
        // all to disk - not a report, and not a log file either.
        var files = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
            : [];

        await Assert.That(files).IsEmpty();
    }

    [Test]
    public async Task The_html_report_embeds_its_assets_and_its_view_model()
    {
        const string folder = "./reports-html";
        DeleteFolder(folder);

        var stats = AutobahnRunner
            .RegisterScenarios(ShortScenario("test", "ok step"))
            .WithReportFolder(folder)
            .WithReportFormats(ReportFormat.Html)
            .Run();

        var html = stats.ReportFiles.Single(x => x.ReportFormat == ReportFormat.Html).ReportContent;

        await Assert.That(html).StartsWith("<!DOCTYPE");
        await Assert.That(html).Contains("const viewModel = {");
        await Assert.That(html).Contains("<style>");
        await Assert.That(html).Contains("<script>");

        // Nothing is left pointing at an embedded asset that is now inlined in the document.
        await Assert.That(html).DoesNotContain("src=\"assets/");
        await Assert.That(html).DoesNotContain("href=\"assets/");
    }
}
