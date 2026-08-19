using Autobahn.Stats;
using Autobahn.Ui.Contracts;

namespace Autobahn.Cli.Ui;

/// <summary>
/// Reads previous runs back out of the report folder, so this one can be compared with them.
/// </summary>
/// <remarks>
/// The run artifact is the only input: it is versioned and machine-readable precisely so that
/// something other than the run that produced it can read it, and the txt/csv/md/html
/// renderings are not parsed here or anywhere.
///
/// Read on request rather than indexed at startup. A report folder with two hundred runs in it
/// is a directory listing and two hundred small files, and doing that work when someone opens
/// the comparison screen costs nothing the run can feel - which is the constraint this whole
/// surface is under.
/// </remarks>
internal static class PastRuns
{
    /// <summary>Every run found beside this one, newest first.</summary>
    public static PastRunSummary[] List(string reportFolder, string currentSessionId)
    {
        var root = Root(reportFolder);
        if (root is null) return [];

        var runs = new List<PastRunSummary>();

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (Read(folder) is not { } artifact) continue;

            runs.Add(Summarise(Path.GetFileName(folder), artifact, currentSessionId));
        }

        return [.. runs.OrderByDescending(x => x.CompletedAtEpochMs)];
    }

    /// <summary>One run in the detail a comparison needs, or null when there is no such run.</summary>
    public static PastRunDetail? Detail(string reportFolder, string id, string currentSessionId)
    {
        var root = Root(reportFolder);
        if (root is null) return null;

        // The id comes off the wire and names a folder, so it is checked rather than trusted:
        // anything with a separator or a parent segment in it is not a run.
        if (id.Length == 0 || id.Contains('/') || id.Contains('\\') || id.Contains("..")) return null;

        var folder = Path.Combine(root, id);
        if (!Directory.Exists(folder)) return null;

        if (Read(folder) is not { } artifact) return null;

        return new PastRunDetail
        {
            Summary = Summarise(id, artifact, currentSessionId),
            Scenarios =
            [
                .. artifact.Result.FinalStats.ScenarioStats.Select(scn => new PastScenario
                {
                    ScenarioName = scn.ScenarioName,
                    Ok = FrameBuilder.Measurement(scn.Ok),
                    Fail = FrameBuilder.Measurement(scn.Fail),
                    Steps =
                    [
                        .. scn.StepStats.Select(step => new PastStep
                        {
                            StepName = step.StepName,
                            Ok = FrameBuilder.Measurement(step.Ok),
                            Fail = FrameBuilder.Measurement(step.Fail)
                        })
                    ]
                })
            ]
        };
    }

    /// <summary>
    /// Where the runs live: the folder holding this run's folder.
    /// </summary>
    /// <remarks>
    /// The default report folder is <c>reports/&lt;session id&gt;</c>, so its parent is the
    /// history. A run pointed at a fixed folder has no siblings and no history, which is the
    /// honest answer rather than a reason to go looking further up the tree.
    /// </remarks>
    private static string? Root(string reportFolder)
    {
        try
        {
            var parent = Directory.GetParent(Path.GetFullPath(reportFolder));
            return parent is not null && parent.Exists ? parent.FullName : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The run artifact in one folder, or null when there is not one.
    /// </summary>
    /// <remarks>
    /// Every json file is tried rather than one by name, because the report file name is a
    /// setting: a folder can hold a run written under any name the caller chose. A json file
    /// that is not an artifact is skipped without complaint - the folder is the user's.
    /// </remarks>
    private static RunArtifact? Read(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                if (RunArtifact.TryRead(File.ReadAllText(file), out var artifact)) return artifact;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static PastRunSummary Summarise(string id, RunArtifact artifact, string currentSessionId)
    {
        var stats = artifact.Result.FinalStats;
        var scenarios = stats.ScenarioStats;

        return new PastRunSummary
        {
            Id = id,
            TestSuite = stats.TestInfo.TestSuite,
            TestName = stats.TestInfo.TestName,
            CompletedAtEpochMs = artifact.CompletedAt.ToUnixTimeMilliseconds(),
            DurationSeconds = stats.Duration.TotalSeconds,
            Ok = stats.AllOkCount,
            Fail = stats.AllFailCount,
            Rps = scenarios.Sum(x => x.Ok.Request.RPS),
            // The worst percentile across scenarios, not the mean of them: a mean hides the one
            // scenario that was in trouble behind the three that were not.
            P95Ms = scenarios.Length == 0 ? 0 : scenarios.Max(x => x.Ok.Latency.Percent95),
            IsCurrent = stats.TestInfo.SessionId == currentSessionId,
            ThresholdsPassed = stats.Thresholds.Length == 0 ? null : stats.AllThresholdsPassed
        };
    }
}
