using Autobahn.Internal.Domain;
using Autobahn.Internal.Json;
using Autobahn.Stats;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Autobahn.Internal.Services.Reports;

/// <summary>
/// The run artifact: the whole result as one versioned, machine-readable document.
/// </summary>
/// <remarks>
/// Written indented, unlike the view model embedded in the HTML report. This one is meant to
/// be read by a person as well as a machine - diffed in a pull request, grepped in a CI log -
/// and the size difference is nothing next to a report folder.
/// </remarks>
internal static class JsonReport
{
    public static string Print(
        ILogger logger, SessionResult sessionResult, IReadOnlyList<RuntimeScenario> targetScenarios)
    {
        try
        {
            logger.ZLogTrace($"JsonReport.print");

            var artifact = new RunArtifact
            {
                SchemaVersion = Constants.RunArtifactSchemaVersion,
                Producer = $"Autobahn {sessionResult.FinalStats.HostInfo.AutobahnVersion}",
                CompletedAt = DateTimeOffset.UtcNow,
                Result = sessionResult,
                Plans = targetScenarios
                    .Select(scn => new ScenarioPlan
                    {
                        ScenarioName = scn.ScenarioName,
                        LoadSimulations = scn.LoadSimulations.Select(x => x.Value).ToArray()
                    })
                    .OrderBy(x => x.ScenarioName, StringComparer.Ordinal)
                    .ToArray()
            };

            return AutobahnJson.SerializeArtifact(artifact);
        }
        catch (Exception ex)
        {
            logger.ZLogError($"JsonReport.print failed: {ex}");
            return "Could not generate report";
        }
    }
}
