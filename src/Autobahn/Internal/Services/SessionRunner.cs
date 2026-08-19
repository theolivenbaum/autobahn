using Autobahn.Internal.Infra;
using Autobahn.Internal.Services.Reports;
using Autobahn.Stats;

namespace Autobahn.Internal.Services;

/// <summary>The session entry point: sets up the run, executes it, and writes the reports.</summary>
internal static class SessionRunner
{
    public static async Task<Result<SessionResult>> RunSession(
        TestInfo testInfo, HostInfo hostInfo, AutobahnContext context, IGlobalDependency dep)
    {
        if (dep.ApplicationType == ApplicationType.Console)
        {
            ConsoleRender.Render(ConsoleRender.AddLogo(Constants.Logo));
        }
        else
        {
            // No terminal to draw on, so no banner - but the report tables still have to
            // be legible in whatever captured this output.
            ConsoleRender.UseFixedWidth(Constants.NonInteractiveConsoleWidth);
        }

        dep.LogInfo(string.Format(Constants.WelcomeText, hostInfo.AutobahnVersion, testInfo.SessionId));

        var scenarios = ContextResolver.CreateScenarios(context);
        if (scenarios.IsError) return Result<SessionResult>.Fail(scenarios.Error);

        var sessionArgs = ContextResolver.CreateSessionArgs(testInfo, context);
        if (sessionArgs.IsError) return Result<SessionResult>.Fail(sessionArgs.Error);

        using var testHost = new TestHost.TestHost(dep, scenarios.Value);

        var sessionResult = await testHost.RunSession(sessionArgs.Value).ConfigureAwait(false);
        if (sessionResult.IsError) return sessionResult;

        var report = Report.Build(dep.Logger, sessionResult.Value, testHost.TargetScenarios);
        var finalStats = Report.Save(dep, sessionArgs.Value, sessionResult.Value.FinalStats, report);

        ApplyThresholdVerdict(dep, sessionArgs.Value, finalStats);

        return Result<SessionResult>.Ok(sessionResult.Value with { FinalStats = finalStats });
    }

    /// <summary>
    /// Says out loud whether the run passed its own rules, and makes the process say so too.
    /// </summary>
    /// <remarks>
    /// A library cannot decide when a process exits, so the contract is the exit code rather
    /// than an exception: Autobahn sets <see cref="Constants.ThresholdFailedExitCode"/> and
    /// leaves the caller's Main to return normally. A CI gate that always exits zero is
    /// decorative, and throwing instead would take the reports with it.
    /// </remarks>
    private static void ApplyThresholdVerdict(IGlobalDependency dep, SessionArgs sessionArgs, SessionStats stats)
    {
        if (stats.Thresholds.Length == 0) return;

        var failed = stats.Thresholds.Where(x => !x.Passed).ToArray();

        if (failed.Length == 0)
        {
            dep.LogInfo($"All {stats.Thresholds.Length} threshold(s) passed.");
            return;
        }

        foreach (var threshold in failed)
        {
            dep.LogError(
                $"Threshold failed: {threshold.Name} - observed {threshold.ObservedValue} "
                + $"({threshold.FailedChecks} of {threshold.TotalChecks} checks failed"
                + $"{(threshold.FirstFailedAt is { } at ? $", first at {at:hh\\:mm\\:ss}" : "")}).");
        }

        if (sessionArgs.EnableThresholdExitCode) Environment.ExitCode = Constants.ThresholdFailedExitCode;
    }

    public static Result<SessionResult> Run(bool disposeLogger, AutobahnContext context)
    {
        var testInfo = new TestInfo
        {
            SessionId = HostInfoProvider.CreateSessionId(),
            TestSuite = ContextResolver.GetTestSuite(context),
            TestName = ContextResolver.GetTestName(context)
        };

        var hostInfo = HostInfoProvider.Init();
        var appType = HostInfoProvider.GetApplicationType();
        var reportFolder = ContextResolver.GetReportFolderOrDefault(testInfo.SessionId, context);

        var logSettings = new LoggerInitSettings { Folder = reportFolder, TestInfo = testInfo };
        var dep = new GlobalDependency(appType, logSettings, context);

        try
        {
            var result = RunSession(testInfo, hostInfo, context, dep).GetAwaiter().GetResult();

            if (result.IsError) dep.LogError(result.Error.Message);

            return result;
        }
        finally
        {
            if (disposeLogger) dep.Dispose();
        }
    }
}
