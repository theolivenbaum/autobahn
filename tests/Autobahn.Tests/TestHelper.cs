using System.Collections.Concurrent;
using System.Data;
using Microsoft.Extensions.Logging;

namespace Autobahn.Tests;

/// <summary>
/// Collects log messages in memory so a test can assert on what a run reported.
/// </summary>
/// <remarks>
/// Hand-written rather than pulled from a package: the tests need one thing from a log sink -
/// the formatted message, synchronously, with no flush to wait on - and a background-flushing
/// provider makes that a race.
/// </remarks>
public sealed class InMemoryLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogRecord> _records = new();

    public IReadOnlyCollection<LogRecord> Records => _records;

    public bool HasMessage(string message) => _records.Any(x => x.Message == message);

    public bool HasMessageContaining(string fragment) => _records.Any(x => x.Message.Contains(fragment));

    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(_records);

    public void Dispose() { }

    public sealed record LogRecord(LogLevel Level, string Message);

    private sealed class InMemoryLogger(ConcurrentQueue<LogRecord> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            records.Enqueue(new LogRecord(logLevel, formatter(state, exception)));
    }
}

/// <summary>Builds the DataSet a worker plugin would return, for the report tests.</summary>
public static class PluginStatisticsHelper
{
    public static DataSet CreatePluginStats()
    {
        var pluginStats = new DataSet();
        pluginStats.Tables.Add(CreateTable("PluginStatistics1"));
        pluginStats.Tables.Add(CreateTable("PluginStatistics2"));
        return pluginStats;
    }

    private static DataTable CreateTable(string prefix)
    {
        var table = new DataTable($"{prefix}Table");

        table.Columns.AddRange(
        [
            new DataColumn("Key", typeof(string)) { Caption = $"{prefix}ColumnKey" },
            new DataColumn("Value", typeof(string)) { Caption = $"{prefix}ColumnValue" },
            new DataColumn("Type", typeof(string)) { Caption = $"{prefix}ColumnType" }
        ]);

        for (var i = 1; i <= 10; i++)
        {
            var row = table.NewRow();
            row["Key"] = $"{prefix}RowKey{i}";
            row["Value"] = $"{prefix}RowValue{i}";
            row["Type"] = $"{prefix}RowType{i}";
            table.Rows.Add(row);
        }

        return table;
    }
}

/// <summary>Scenarios shared by more than one test file.</summary>
public static class PluginTestHelper
{
    public static ScenarioProps[] CreateScenarios()
    {
        var scenario1 = Scenario.Create("scenario 1", async ctx =>
        {
            await Step.Run("step 1", ctx, async () =>
            {
                await Task.Delay(Time.Seconds(0.1));
                return Response.Ok();
            });

            await Step.Run("step 2", ctx, async () =>
            {
                await Task.Delay(Time.Seconds(0.2));
                return Response.Ok();
            });

            await Step.Run("step 3", ctx, async () =>
            {
                await Task.Delay(Time.Seconds(0.3));
                return Response.Ok();
            });

            return Response.Ok();
        })
        .WithWarmUpDuration(Time.Seconds(2))
        .WithLoadSimulations(Simulation.KeepConstant(copies: 2, during: Time.Seconds(10)));

        var scenario2 = Scenario.Create("scenario 2", async ctx =>
        {
            await Task.Delay(Time.Seconds(0.3));
            return Response.Ok();
        })
        .WithWarmUpDuration(Time.Seconds(2))
        .WithLoadSimulations(Simulation.KeepConstant(copies: 2, during: Time.Seconds(10)));

        return [scenario1, scenario2];
    }
}
