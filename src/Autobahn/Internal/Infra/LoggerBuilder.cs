using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Providers;
using Autobahn.Stats;

namespace Autobahn.Internal.Infra;

/// <summary>How this run's loggers are wired.</summary>
internal sealed record LoggerInitSettings
{
    /// <summary>Where the run's log file goes. Wiped before the run so a folder holds one run.</summary>
    public required string Folder { get; init; }

    public required TestInfo TestInfo { get; init; }
}

/// <summary>
/// Builds the two loggers a run uses: one for the console and one for the log file.
/// </summary>
/// <remarks>
/// Both are ZLogger providers behind Microsoft.Extensions.Logging, so a user can add their
/// own providers with <c>AutobahnRunner.WithLogging</c> or configure levels from the infra
/// config's <c>Logging</c> section, and a scenario's <c>ctx.Logger</c> is just an
/// <c>ILogger</c> that any .NET codebase already knows how to deal with.
/// </remarks>
internal static class LoggerBuilder
{

    public static ILoggerFactory CreateConsoleLoggerFactory() =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);

            builder.AddZLoggerConsole(options =>
            {
                options.UsePlainTextFormatter(formatter =>
                {
                    formatter.SetPrefixFormatter($"{0:HH:mm:ss} [{1}] ", (in MessageTemplate template, in LogInfo info) =>
                        template.Format(info.Timestamp.Local, LevelName(info.LogLevel)));
                });
            });
        });

    public static ILoggerFactory CreateLoggerFactory(LoggerInitSettings settings, AutobahnContext context)
    {
        CleanFolder(settings.Folder);

        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(context.MinimumLogLevel ?? LogLevel.Debug);

            if (context.ConfigureLogging is { } configure)
            {
                // The user took over: their providers replace the default file log entirely.
                configure(builder);
            }
            else
            {
                AttachDefaultFileLogger(builder, settings.Folder);
            }

            // The infra config can raise or lower levels per category without touching code.
            if (context.InfraConfig?.GetSection("Logging") is { } loggingSection && loggingSection.Exists())
                builder.AddConfiguration(loggingSection);
        });
    }

    private static void AttachDefaultFileLogger(ILoggingBuilder builder, string folder)
    {
        builder.AddZLoggerRollingFile(options =>
        {
            options.FilePathSelector = (timestamp, sequence) =>
                Path.Combine(folder, $"{Constants.LogFilePrefix}-{timestamp:yyyy-MM-dd}_{sequence}.txt");

            options.RollingInterval = RollingInterval.Day;
            options.RollingSizeKB = 1024 * 100;

            options.UsePlainTextFormatter(formatter =>
            {
                formatter.SetPrefixFormatter($"{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] ", (in MessageTemplate template, in LogInfo info) =>
                    template.Format(info.Timestamp.Local, LevelName(info.LogLevel)));
            });
        });
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "NON"
    };

    /// <summary>
    /// Empties the run's output folder before the run starts.
    /// </summary>
    /// <remarks>
    /// Inherited behaviour, kept for parity, and a sharp edge: with the default folder
    /// (<c>reports/{sessionId}</c>) this is a no-op because the folder is new every run, but
    /// a pinned <c>WithReportFolder</c> is deleted recursively on every run. Narrowing it to
    /// deleting only the files Autobahn itself wrote is TODO.md section 5.
    /// </remarks>
    private static void CleanFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // A folder we cannot clear is not a reason to fail the run; the log just appends.
        }
    }
}
