using Microsoft.Extensions.Logging;

namespace Autobahn.Tests;

[NotInParallel]
public class LoggingTests
{
    private static ScenarioProps LoggingScenario(string name) =>
        Scenario.Create(name, async ctx =>
            {
                await Task.Delay(Time.Milliseconds(100));
                ctx.Logger.LogInformation("a message from inside the scenario");
                return Response.Ok();
            })
            .WithLoadSimulations(Simulation.KeepConstant(1, Time.Seconds(3)))
            .WithoutWarmUp();

    [Test]
    public async Task The_minimum_log_level_decides_what_a_scenario_can_write()
    {
        var quiet = new InMemoryLoggerProvider();
        var verbose = new InMemoryLoggerProvider();

        AutobahnRunner
            .RegisterScenarios(LoggingScenario("scenario1"))
            .WithMinimumLogLevel(LogLevel.Error)
            .WithLogging(builder => builder.AddProvider(quiet))
            .WithoutReports()
            .Run("disposeLogger=false");

        AutobahnRunner
            .RegisterScenarios(LoggingScenario("scenario2"))
            .WithMinimumLogLevel(LogLevel.Trace)
            .WithLogging(builder => builder.AddProvider(verbose))
            .WithoutReports()
            .Run("disposeLogger=false");

        await Assert.That(quiet.HasMessage("a message from inside the scenario")).IsFalse();
        await Assert.That(verbose.HasMessage("a message from inside the scenario")).IsTrue();
    }

    [Test]
    public async Task A_run_writes_its_own_log_file_next_to_the_reports()
    {
        const string folder = "./reports-logging";
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);

        AutobahnRunner
            .RegisterScenarios(LoggingScenario("scenario"))
            .WithReportFolder(folder)
            .WithoutReports()
            .Run();

        var logFiles = Directory.GetFiles(folder, $"{Constants.LogFilePrefix}*");

        await Assert.That(logFiles.Length).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(logFiles[0])).Contains("Starting bombing...");
    }

    [Test]
    public async Task The_session_progress_reaches_the_log()
    {
        var logs = new InMemoryLoggerProvider();

        AutobahnRunner
            .RegisterScenarios(LoggingScenario("scenario"))
            .WithLogging(builder => builder.AddProvider(logs))
            .WithoutReports()
            .Run("disposeLogger=false");

        await Assert.That(logs.HasMessageContaining("started a new session")).IsTrue();
        await Assert.That(logs.HasMessage("Starting init...")).IsTrue();
        await Assert.That(logs.HasMessage("Init finished")).IsTrue();
        await Assert.That(logs.HasMessage("Starting bombing...")).IsTrue();
        await Assert.That(logs.HasMessage("Calculating final statistics...")).IsTrue();
    }
}
