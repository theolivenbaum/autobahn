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

        return Result<SessionResult>.Ok(sessionResult.Value with { FinalStats = finalStats });
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
