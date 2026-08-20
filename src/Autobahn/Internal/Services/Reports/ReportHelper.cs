using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Metrics;
using Autobahn.Stats;
using Autobahn.Thresholds;

namespace Autobahn.Internal.Services.Reports;

/// <summary>The parts every report format renders the same way, differing only in how they colour text.</summary>
internal static class ReportHelper
{
    public static string PrintDataKb(Func<object?, string> highlight, long bytes) =>
        $"{highlight(Converter.FromBytesToKb(bytes))} KB";

    public static string PrintAllData(Func<object?, string> highlight, long bytes) =>
        $"{highlight(Converter.FromBytesToMb(bytes))} MB";

    public static string GetFullReportsFolderPath(SessionArgs sessionArgs)
    {
        var exeFilePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var workPath = Path.GetDirectoryName(exeFilePath) ?? Directory.GetCurrentDirectory();
        return Path.Combine(workPath, sessionArgs.ReportFolder);
    }

    /// <summary>One step rendered as label/value rows, which every format lays out its own way.</summary>
    public static List<List<string>> PrintStepStatsRow(
        bool isOkStats,
        Func<object?, string> okColor,
        Func<object?, string> errorColor,
        Func<object?, string> blueColor,
        int stepIndex,
        StepStats stats)
    {
        var highlight = isOkStats ? okColor : errorColor;
        var data = isOkStats ? stats.Ok : stats.Fail;

        var rq = data.Request;
        var lt = data.Latency;
        var dt = data.DataTransfer;
        var allReqCount = Statistics.GetAllRequestCount(stats);

        var reqCount = isOkStats
            ? $"all = {okColor(allReqCount)}, ok = {okColor(rq.Count)}, RPS = {okColor(rq.RPS)}"
            : $"all = {okColor(allReqCount)}, fail = {errorColor(rq.Count)}, RPS = {errorColor(rq.RPS)}";

        var latencies =
            $"min = {highlight(lt.MinMs)}, mean = {highlight(lt.MeanMs)}, max = {highlight(lt.MaxMs)}, StdDev = {highlight(lt.StdDev)}";

        var percentiles =
            $"p50 = {highlight(lt.Percent50)}, p75 = {highlight(lt.Percent75)}, p95 = {highlight(lt.Percent95)}, p99 = {highlight(lt.Percent99)}";

        var dataTransfer =
            $"min = {PrintDataKb(highlight, dt.MinBytes)}, mean = {PrintDataKb(highlight, dt.MeanBytes)}, "
            + $"max = {PrintDataKb(highlight, dt.MaxBytes)}, all = {PrintAllData(highlight, dt.AllBytes)}";

        var rows = new List<List<string>>();

        if (stepIndex > 0)
            rows.Add([string.Empty, string.Empty]);

        rows.Add(["name", blueColor(stats.StepName)]);
        rows.Add(["request count", reqCount]);
        rows.Add(["latency", latencies]);
        rows.Add(["latency percentile", percentiles]);

        if (data.DataTransfer.AllBytes > 0)
            rows.Add(["data transfer", dataTransfer]);

        return rows;
    }

    public static string PrintLoadSimulation(Func<object?, string> okColor, LoadSimulation simulation)
    {
        var name = SimulationPlan.GetSimulationName(simulation);

        var values = simulation switch
        {
            LoadSimulation.RampingConstant x => [("copies", (object)x.Copies), ("during", x.During)],
            LoadSimulation.KeepConstant x => [("copies", (object)x.Copies), ("during", x.During)],
            LoadSimulation.RampingInject x => [("rate", (object)x.Rate), ("interval", x.Interval), ("during", x.During)],
            LoadSimulation.Inject x => [("rate", (object)x.Rate), ("interval", x.Interval), ("during", x.During)],
            LoadSimulation.InjectRandom x =>
                [("minRate", (object)x.MinRate), ("maxRate", x.MaxRate), ("interval", x.Interval), ("during", x.During)],
            LoadSimulation.IterationsForConstant x => [("copies", (object)x.Copies), ("iterations", x.Iterations)],
            LoadSimulation.IterationsForInject x =>
                [("rate", (object)x.Rate), ("interval", x.Interval), ("iterations", x.Iterations)],
            LoadSimulation.Pause x => new (string, object)[] { ("during", x.During) },
            _ => throw new NotSupportedException($"Unknown load simulation: {simulation.GetType().Name}")
        };

        var argsStr = values.Select(v => $"{v.Item1}: {okColor(v.Item2)}").ConcatWithComma();
        return $"  - {okColor(name)}, {argsStr}";
    }

    /// <summary>The column headings of the metrics table, shared by every format.</summary>
    public static string[] MetricTableHeaders { get; } =
        ["metric", "kind", "unit", "current", "min", "mean", "max", "p50", "p95", "p99", "writes"];

    /// <summary>
    /// Metrics as table rows, in the order the stats already carry - which is by name, so a
    /// diff between two runs is a diff of values rather than of row order.
    /// </summary>
    /// <remarks>
    /// A counter has no distribution and a gauge has no meaningful percentiles, so the cells
    /// that would be repeating the same number are blanked instead. A row that prints a value
    /// for every column would suggest the metric measured something it did not.
    /// </remarks>
    public static List<List<string>> CreateMetricTableRows(
        Func<object?, string> highlight, Func<object?, string> blueColor, IReadOnlyList<MetricStats> metrics)
    {
        var rows = new List<List<string>>(metrics.Count);

        foreach (var m in metrics)
        {
            var isCounter = m.Kind == MetricKind.Counter;
            var hasPercentiles = m.Kind == MetricKind.Histogram;

            rows.Add(
            [
                blueColor(m.Name),
                m.Kind.ToString().ToLowerInvariant(),
                m.Unit,
                highlight(m.Current),
                isCounter ? "" : highlight(m.Min),
                isCounter ? "" : highlight(m.Mean),
                isCounter ? "" : highlight(m.Max),
                hasPercentiles ? highlight(m.Percent50) : "",
                hasPercentiles ? highlight(m.Percent95) : "",
                hasPercentiles ? highlight(m.Percent99) : "",
                m.Count.ToString()
            ]);
        }

        return rows;
    }

    /// <summary>The column headings of the thresholds table, shared by every format.</summary>
    public static string[] ThresholdTableHeaders { get; } =
        ["threshold", "scenario", "target", "observed", "checks failed", "first failed at", "verdict"];

    /// <summary>
    /// Thresholds as table rows, in the order the stats carry them - by name, so a diff between
    /// two runs is a diff of verdicts rather than of row order.
    /// </summary>
    public static List<List<string>> CreateThresholdTableRows(
        Func<object?, string> okColor,
        Func<object?, string> errorColor,
        Func<object?, string> blueColor,
        IReadOnlyList<ThresholdResult> thresholds)
    {
        var rows = new List<List<string>>(thresholds.Count);

        foreach (var t in thresholds)
        {
            var highlight = t.Passed ? okColor : errorColor;

            var verdict = t.Aborted
                ? errorColor("failed - aborted the run")
                : t.Passed ? okColor("passed") : errorColor("failed");

            rows.Add(
            [
                blueColor(t.Name),
                string.IsNullOrEmpty(t.ScenarioName) ? "" : t.ScenarioName,
                $"{Symbol(t.Comparison)} {t.Value}",
                highlight(t.ObservedValue),
                $"{t.FailedChecks} of {t.TotalChecks}",
                t.FirstFailedAt is { } at ? $"{at:hh\\:mm\\:ss}" : "",
                verdict
            ]);
        }

        return rows;
    }

    public static string Symbol(ThresholdComparison comparison) => comparison switch
    {
        ThresholdComparison.LessThan => "<",
        ThresholdComparison.LessThanOrEqual => "<=",
        ThresholdComparison.GreaterThan => ">",
        ThresholdComparison.GreaterThanOrEqual => ">=",
        _ => "?"
    };

    /// <summary>Status codes as table rows, with a trailing row for requests that reported none.</summary>
    public static List<List<string>> CreateStatusCodeTableRows(
        Func<object?, string> okColor, Func<object?, string> errorColor, ScenarioStats scnStats)
    {
        var rows = scnStats.Ok.StatusCodes
            .Select(x => new List<string> { okColor(x.StatusCode), x.Count.ToString(), x.Message })
            .ToList();

        rows.AddRange(scnStats.Fail.StatusCodes
            .Select(x => new List<string> { errorColor(x.StatusCode), x.Count.ToString(), errorColor(x.Message) }));

        var okStatusCount = scnStats.Ok.StatusCodes.Sum(x => x.Count);
        var failStatusCount = scnStats.Fail.StatusCodes.Sum(x => x.Count);
        var noStatusCount = scnStats.AllRequestCount - (okStatusCount + failStatusCount);

        if (noStatusCount > 0)
            rows.Add([okColor("no status"), noStatusCount.ToString(), ""]);

        return rows;
    }
}
