using Autobahn.Stats;

namespace Autobahn.Internal.Domain;

/// <summary>Reads the final stats and points out ways the test itself was under-instrumented.</summary>
internal static class HintsAnalyzer
{
    private static IEnumerable<HintResult> AnalyzeDataTransfer(ScenarioStats scnStats) =>
        scnStats.StepStats
            .Where(step => step.Ok.DataTransfer.MinBytes + step.Fail.DataTransfer.MinBytes == 0)
            .Select(step => new HintResult
            {
                SourceName = scnStats.ScenarioName,
                SourceType = HintSourceType.Scenario,
                Hint = $"Step: '{step.StepName}' in Scenario: '{scnStats.ScenarioName}' didn't track data transfer."
                       + " In order to track data transfer, you should use Response.Ok(sizeBytes: value)"
            });

    private static IEnumerable<HintResult> AnalyzeStatusCodes(ScenarioStats scnStats) =>
        scnStats.StepStats
            .Where(step =>
                (step.Ok.Request.Count > 0 && step.Ok.StatusCodes.Length == 0) ||
                (step.Fail.Request.Count > 0 && step.Fail.StatusCodes.Length == 0))
            .Select(step => new HintResult
            {
                SourceName = scnStats.ScenarioName,
                SourceType = HintSourceType.Scenario,
                Hint = $"Step: '{step.StepName}' in Scenario: '{scnStats.ScenarioName}' didn't track status code."
                       + " In order to track status code, you should use Response.Ok(statusCode: value)"
            });

    public static List<HintResult> AnalyzeSessionStats(SessionStats stats) =>
        stats.ScenarioStats
            .SelectMany(scnStats => AnalyzeStatusCodes(scnStats).Concat(AnalyzeDataTransfer(scnStats)))
            .ToList();
}
