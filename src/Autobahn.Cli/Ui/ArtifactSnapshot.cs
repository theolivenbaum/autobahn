using Autobahn.Stats;
using Autobahn.Ui.Contracts;

namespace Autobahn.Cli.Ui;

/// <summary>
/// Turns a finished run's artifact into the same snapshot the live host serves.
/// </summary>
/// <remarks>
/// The point of doing it this way is that the exported page is not a second application: it
/// is the same one, handed a snapshot from a file instead of from a socket. A separate
/// renderer for finished runs is a renderer that drifts from the live one, and the two
/// disagreeing about what a run's numbers were is exactly the failure this is meant to avoid.
/// </remarks>
internal static class ArtifactSnapshot
{
    public static RunSnapshot Build(RunArtifact artifact, string reportFolder)
    {
        var stats = artifact.Result.FinalStats;
        var completedAtMs = artifact.CompletedAt.ToUnixTimeMilliseconds();
        var startedAtMs = completedAtMs - (long)stats.Duration.TotalMilliseconds;

        var history = artifact.Result.TimeLineHistory
            .Select(record => FrameBuilder.Frame(
                record,
                RunState.Bombing,
                $"{record.Duration:hh\\:mm\\:ss} elapsed",
                startedAtMs + (long)record.Duration.TotalMilliseconds,
                record.Thresholds,
                []))
            .Select((frame, index) => frame with { Sequence = index + 1 })
            .ToArray();

        var final = Final(stats, completedAtMs, history.Length + 1);

        return new RunSnapshot
        {
            Run = Describe(artifact, startedAtMs),
            History = history,
            Latest = final,
            Reports = Reports(reportFolder)
        };
    }

    /// <summary>
    /// The run's last word, as the frame the live view would have ended on.
    /// </summary>
    /// <remarks>
    /// Built rather than taken from the timeline, because the timeline records intervals and
    /// this records the verdict: the thresholds here were checked against the whole run, not
    /// against its last five seconds.
    /// </remarks>
    private static LiveFrame Final(SessionStats stats, long completedAtMs, int sequence)
    {
        var verdict = stats.Thresholds.Length == 0
            ? "Finished."
            : stats.AllThresholdsPassed
                ? $"Finished. All {stats.Thresholds.Length} threshold(s) passed."
                : $"Finished. {stats.Thresholds.Count(x => !x.Passed)} of {stats.Thresholds.Length} threshold(s) failed.";

        return new LiveFrame
        {
            Sequence = sequence,
            ElapsedSeconds = stats.Duration.TotalSeconds,
            TimestampEpochMs = completedAtMs,
            State = stats.AllThresholdsPassed ? RunState.Finished : RunState.Failed,
            StatusText = verdict,

            // The final scenario statistics, so the tiles read the run's totals rather than
            // whatever the last interval happened to hold.
            Scenarios = [.. stats.ScenarioStats.Select(FrameBuilder.Scenario)],
            Metrics = [.. stats.Metrics.Select(FrameBuilder.Metric)],
            Thresholds = [.. stats.Thresholds.Select(FrameBuilder.Threshold)]
        };
    }

    private static RunDescriptor Describe(RunArtifact artifact, long startedAtMs)
    {
        var stats = artifact.Result.FinalStats;
        var plans = artifact.Plans.ToDictionary(x => x.ScenarioName, x => x.LoadSimulations);

        var scenarios = stats.ScenarioStats
            .Select(scn => Scenario(scn, plans.TryGetValue(scn.ScenarioName, out var plan) ? plan : []))
            .ToArray();

        return new RunDescriptor
        {
            SessionId = stats.TestInfo.SessionId,
            TestSuite = stats.TestInfo.TestSuite,
            TestName = stats.TestInfo.TestName,
            StartedAtEpochMs = startedAtMs,
            PlannedDurationSeconds = stats.Duration.TotalSeconds,
            Host = new HostDescriptor
            {
                MachineName = stats.HostInfo.MachineName,
                OperatingSystem = stats.HostInfo.OS,
                Architecture = stats.HostInfo.Processor,
                ProcessorCount = stats.HostInfo.CoresCount,
                AutobahnVersion = stats.HostInfo.AutobahnVersion
            },
            Scenarios = scenarios,

            // The artifact records what a run measured, not how it was configured: the
            // provenance log is a live-run thing, and inventing entries for it here would be
            // worse than an empty configuration screen that says so.
            Settings = [],
            Thresholds = [.. stats.Thresholds.Select(Declared)]
        };
    }

    private static ScenarioDescriptor Scenario(ScenarioStats stats, IReadOnlyList<LoadSimulation> plan) => new()
    {
        ScenarioName = stats.ScenarioName,
        PlannedDurationSeconds = stats.Duration.TotalSeconds,
        MaxCopies = plan.Count == 0 ? stats.LoadSimulationStats.Value : plan.Max(Level),
        Plan = FrameBuilder.Plan(plan)
    };

    /// <summary>
    /// A threshold as the descriptor screen reads it, from its result.
    /// </summary>
    /// <remarks>
    /// The artifact keeps each rule's outcome rather than its declaration, and a result carries
    /// everything the rule asserted - so the only fields left empty are the ones about when
    /// checking started and what would abort the run, which a finished run cannot be asked.
    /// </remarks>
    private static ThresholdDescriptor Declared(ThresholdResult result) => new()
    {
        Name = result.Name,
        ScenarioName = result.ScenarioName,
        Scope = result.Scope.ToString(),
        Subject = result.Subject.ToString(),
        Comparison = result.Comparison switch
        {
            Autobahn.Thresholds.ThresholdComparison.LessThan => "<",
            Autobahn.Thresholds.ThresholdComparison.LessThanOrEqual => "<=",
            Autobahn.Thresholds.ThresholdComparison.GreaterThan => ">",
            Autobahn.Thresholds.ThresholdComparison.GreaterThanOrEqual => ">=",
            _ => "?"
        },
        Target = result.Value
    };

    private static int Level(LoadSimulation simulation) => simulation switch
    {
        LoadSimulation.RampingConstant x => x.Copies,
        LoadSimulation.KeepConstant x => x.Copies,
        LoadSimulation.IterationsForConstant x => x.Copies,
        LoadSimulation.RampingInject x => x.Rate,
        LoadSimulation.Inject x => x.Rate,
        LoadSimulation.InjectRandom x => x.MaxRate,
        LoadSimulation.IterationsForInject x => x.Rate,
        _ => 0
    };

    /// <summary>
    /// The other files beside the artifact, listed but not linked.
    /// </summary>
    /// <remarks>
    /// An exported page is one file that may be mailed anywhere, so a link to a sibling on
    /// somebody else's disk would be a broken link. Naming them is still worth doing: it says
    /// what else the run produced and where it was produced.
    /// </remarks>
    private static ReportDescriptor[] Reports(string reportFolder)
    {
        try
        {
            return
            [
                .. new DirectoryInfo(reportFolder)
                    .EnumerateFiles()
                    .OrderBy(x => x.Name, StringComparer.Ordinal)
                    .Select(x => new ReportDescriptor
                    {
                        FileName = x.Name,
                        Format = x.Extension.TrimStart('.').ToUpperInvariant(),
                        SizeBytes = x.Length
                    })
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
