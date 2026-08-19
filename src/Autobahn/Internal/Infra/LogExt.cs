using Microsoft.Extensions.Logging;
using ZLogger;

namespace Autobahn.Internal.Infra;

/// <summary>
/// Writes a message to both the console and the log file.
/// </summary>
/// <remarks>
/// The two loggers are separate on purpose: the console shows a run's progress, the file
/// keeps everything. Anything logged through <c>dep.Logger</c> alone stays out of the
/// console; anything logged through these helpers goes to both.
/// </remarks>
internal static class LogExt
{
    public static void LogInfo(this IGlobalDependency dep, string message)
    {
        dep.ConsoleLogger.ZLogInformation($"{message}");
        dep.Logger.ZLogInformation($"{message}");
    }

    public static void LogWarn(this IGlobalDependency dep, string message)
    {
        dep.ConsoleLogger.ZLogWarning($"{message}");
        dep.Logger.ZLogWarning($"{message}");
    }

    public static void LogWarn(this IGlobalDependency dep, Exception ex, string message)
    {
        // The console gets the stack trace only when the file log is set to capture that much.
        if (dep.Logger.IsEnabled(LogLevel.Trace)) dep.ConsoleLogger.ZLogWarning(ex, $"{message}");
        else dep.ConsoleLogger.ZLogWarning($"{message}");

        dep.Logger.ZLogWarning(ex, $"{message}");
    }

    public static void LogError(this IGlobalDependency dep, string message)
    {
        dep.ConsoleLogger.ZLogError($"{message}");
        dep.Logger.ZLogError($"{message}");
    }

    public static void LogError(this IGlobalDependency dep, Exception ex, string message)
    {
        if (dep.Logger.IsEnabled(LogLevel.Trace)) dep.ConsoleLogger.ZLogError(ex, $"{message}");
        else dep.ConsoleLogger.ZLogError($"{message}");

        dep.Logger.ZLogError(ex, $"{message}");
    }

    public static void LogFatal(this IGlobalDependency dep, string message)
    {
        dep.ConsoleLogger.ZLogCritical($"{message}");
        dep.Logger.ZLogCritical($"{message}");
    }
}
