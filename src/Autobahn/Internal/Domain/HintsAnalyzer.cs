using Autobahn.Metrics;
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

    /// <summary>
    /// Reads the runtime metrics and says so when the load generator was its own bottleneck.
    /// </summary>
    /// <remarks>
    /// The most common way a load test lies is that the generator ran out of something and
    /// the resulting queueing was reported as the target getting slower. Autobahn measures
    /// itself precisely so it can say when that happened, and saying it is worth more than
    /// having measured it: nobody reads a runtime metric they had no reason to suspect.
    ///
    /// Every threshold here is deliberately loud rather than precise. A hint that fires on a
    /// healthy run gets ignored, and then so does the one that mattered.
    /// </remarks>
    private static IEnumerable<HintResult> AnalyzeLoadGenerator(SessionStats stats)
    {
        var metrics = stats.Metrics.ToDictionary(x => x.Name, StringComparer.Ordinal);

        // A thread-pool queue that is never empty means work waited before it even started,
        // and that wait is inside every latency the run reported.
        if (Metric(metrics, Constants.MetricThreadPoolQueue) is { Max: > 50 } queue)
        {
            yield return Generator(
                $"The load generator's thread-pool queue reached {queue.Max} items (mean {queue.Mean}). "
                + "Work waited to start, and that wait is inside the latencies this run reported. "
                + "Either the scenario is doing blocking work on a pool thread - look for .Result, "
                + ".Wait() or synchronous I/O - or this machine cannot generate this much load.");
        }

        // Sustained high CPU on the generator means the same thing by a different route.
        if (Metric(metrics, Constants.MetricCpuPercent) is { Mean: > 85 } cpu)
        {
            yield return Generator(
                $"The load generator averaged {cpu.Mean}% CPU across all cores (peak {cpu.Max}%). "
                + "At that level its own scheduling is part of what this run measured. "
                + "Generate the load from more machines, or from a bigger one, before trusting these numbers.");
        }

        // Gen2 collections stop the world, and a stopped world looks exactly like a slow target.
        if (Metric(metrics, Constants.MetricGen2Collections) is { Current: > 10 } gen2)
        {
            yield return Generator(
                $"The load generator ran {gen2.Current} gen2 collections during this run. "
                + "Each one pauses every scenario copy, and the pause is reported as the target's latency. "
                + "Look for per-iteration allocation in the scenario - a payload built per request, "
                + "or a client created rather than reused.");
        }
    }

    private static MetricStats? Metric(IReadOnlyDictionary<string, MetricStats> metrics, string name) =>
        metrics.TryGetValue(name, out var metric) && metric.Count > 0 ? metric : null;

    private static HintResult Generator(string hint) => new()
    {
        SourceName = "load generator",
        SourceType = HintSourceType.LoadGenerator,
        Hint = hint
    };

    public static List<HintResult> AnalyzeSessionStats(SessionStats stats) =>
    [
        .. stats.ScenarioStats.SelectMany(scnStats => AnalyzeStatusCodes(scnStats).Concat(AnalyzeDataTransfer(scnStats))),
        .. AnalyzeLoadGenerator(stats)
    ];
}
