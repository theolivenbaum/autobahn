using Autobahn.Stats;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Providers;

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

            // Added rather than substituted, so something that only wants to watch the log
            // does not stop it being written.
            foreach (var provider in context.AdditionalLoggerProviders) builder.AddProvider(provider);

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
    /// Removes the log files a previous run left in this folder, so a pinned report folder
    /// does not accumulate one log per day forever.
    /// </summary>
    /// <remarks>
    /// The fork point deleted the whole folder recursively. With the default
    /// <c>reports/{sessionId}</c> that is a no-op, because the folder is new every run - but a
    /// pinned <c>WithReportFolder</c> pointed Autobahn at a directory it then destroyed on
    /// every run, along with anything else that happened to be in it. Only the files Autobahn
    /// itself writes are its to delete, and only the log files: reports carry a timestamp in
    /// their name and are meant to accumulate.
    /// </remarks>
    private static void CleanFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;

            foreach (var file in Directory.EnumerateFiles(folder, $"{Constants.LogFilePrefix}-*.txt"))
                File.Delete(file);
        }
        catch
        {
            // A file we cannot remove is not a reason to fail the run; the log just appends.
        }
    }
}
