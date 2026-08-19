using Autobahn.Stats;
using Autobahn.Ui.Contracts;

namespace Autobahn.Cli.Ui;

/// <summary>
/// Turns the engine's stats records into the wire shape the UI reads.
/// </summary>
/// <remarks>
/// A translation layer rather than serializing the engine's own types, so the wire format can
/// stay still while the engine's records move. The two have different jobs: a stats record is
/// what the reports read, and a frame is what a browser can draw without knowing anything
/// about HdrHistogram or load simulations.
/// </remarks>
internal static class FrameBuilder
{
    public static LiveFrame Frame(
        TimeLineHistoryRecord record,
        RunState state,
        string statusText,
        long timestampEpochMs,
        IReadOnlyList<ThresholdResult> thresholds,
        IReadOnlyList<LogLine> logs) => new()
        {
            ElapsedSeconds = record.Duration.TotalSeconds,
            TimestampEpochMs = timestampEpochMs,
            State = state,
            StatusText = statusText,
            Scenarios = record.ScenarioStats.Select(Scenario).ToArray(),
            Metrics = record.Metrics.Select(Metric).ToArray(),
            Thresholds = thresholds.Select(Threshold).ToArray(),
            Logs = [.. logs]
        };

    /// <summary>One scenario's numbers, live or final.</summary>
    public static ScenarioFrame Scenario(ScenarioStats stats) => new()
    {
        ScenarioName = stats.ScenarioName,
        SimulationName = stats.LoadSimulationStats.SimulationName,
        SimulationValue = stats.LoadSimulationStats.Value,

        // What the plan asked for, against what was live. The two diverging is the clearest
        // sign the generator is saturated rather than the target.
        ScheduledCopies = stats.LoadSimulationStats.Value,
        ActualCopies = stats.LoadSimulationStats.ActualCopies,

        Ok = Measurement(stats.Ok),
        Fail = Measurement(stats.Fail),
        Steps = stats.StepStats.Select(Step).ToArray(),
        StatusCodes = stats.Ok.StatusCodes.Concat(stats.Fail.StatusCodes).Select(StatusCode).ToArray()
    };

    private static StepFrame Step(StepStats stats) => new()
    {
        StepName = stats.StepName,
        Ok = Measurement(stats.Ok),
        Fail = Measurement(stats.Fail)
    };

    /// <summary>One side of a scenario or step, in the wire shape. Also what a past run reads.</summary>
    public static MeasurementFrame Measurement(MeasurementStats stats) => new()
    {
        Count = stats.Request.Count,
        Rps = stats.Request.RPS,
        MinMs = stats.Latency.MinMs,
        MeanMs = stats.Latency.MeanMs,
        MaxMs = stats.Latency.MaxMs,
        P50Ms = stats.Latency.Percent50,
        P75Ms = stats.Latency.Percent75,
        P95Ms = stats.Latency.Percent95,
        P99Ms = stats.Latency.Percent99,
        Bytes = stats.DataTransfer.AllBytes
    };

    private static StatusCodeFrame StatusCode(StatusCodeStats stats) => new()
    {
        StatusCode = stats.StatusCode,
        IsError = stats.IsError,
        Message = stats.Message,
        Count = stats.Count
    };

    /// <summary>One metric's numbers, live or final.</summary>
    public static MetricFrame Metric(MetricStats stats) => new()
    {
        Name = stats.Name,
        Kind = stats.Kind.ToString().ToLowerInvariant(),
        Unit = stats.Unit,
        Current = stats.Current,
        Min = stats.Min,
        Mean = stats.Mean,
        Max = stats.Max,
        P50 = stats.Percent50,
        P95 = stats.Percent95,
        P99 = stats.Percent99,
        WriteCount = stats.Count
    };

    /// <summary>One threshold's state, live or final.</summary>
    public static ThresholdFrame Threshold(ThresholdResult result) => new()
    {
        Name = result.Name,
        ScenarioName = result.ScenarioName,
        Observed = result.ObservedValue,
        Passing = result.Passed,
        Checked = result.TotalChecks > 0,
        FailedChecks = result.FailedChecks,
        TotalChecks = result.TotalChecks,
        Aborted = result.Aborted
    };

    public static ThresholdDescriptor Descriptor(Autobahn.Thresholds.Threshold threshold) => new()
    {
        Name = threshold.Describe(),
        Scope = threshold.Scope.ToString(),
        Subject = threshold.Subject.ToString(),
        Comparison = threshold.Comparison switch
        {
            Autobahn.Thresholds.ThresholdComparison.LessThan => "<",
            Autobahn.Thresholds.ThresholdComparison.LessThanOrEqual => "<=",
            Autobahn.Thresholds.ThresholdComparison.GreaterThan => ">",
            Autobahn.Thresholds.ThresholdComparison.GreaterThanOrEqual => ">=",
            _ => "?"
        },
        Target = threshold.Value,
        ScenarioName = threshold.ScenarioName ?? "",
        StartsAfterSeconds = threshold.StartsAfter?.TotalSeconds,
        AbortAfter = threshold.AbortAfter
    };

    /// <summary>
    /// A scenario's load plan laid out on a timeline, so the UI can draw it before the run
    /// starts as well as during.
    /// </summary>
    public static SimulationSegment[] Plan(IReadOnlyList<LoadSimulation> simulations)
    {
        var segments = new List<SimulationSegment>(simulations.Count);
        var start = 0.0;
        var previousLevel = 0;

        foreach (var simulation in simulations)
        {
            var (kind, level, iterations, label) = Describe(simulation);
            var duration = simulation.Duration == TimeSpan.Zero ? (double?)null : simulation.Duration.TotalSeconds;

            segments.Add(new SimulationSegment
            {
                Kind = kind,
                StartSeconds = start,
                DurationSeconds = duration,
                Level = level,
                // A ramp needs to know where it came from to be drawn as a ramp rather than
                // a step, and only the previous segment knows that.
                FromLevel = kind.StartsWith("ramping", StringComparison.Ordinal) ? previousLevel : level,
                Iterations = iterations,
                Label = label
            });

            start += duration ?? 0;
            previousLevel = level;
        }

        return [.. segments];
    }

    private static (string Kind, int Level, int? Iterations, string Label) Describe(LoadSimulation simulation) =>
        simulation switch
        {
            LoadSimulation.RampingConstant x =>
                ("ramping_constant", x.Copies, null, $"ramp to {x.Copies} copies"),
            LoadSimulation.KeepConstant x =>
                ("keep_constant", x.Copies, null, $"hold {x.Copies} copies"),
            LoadSimulation.RampingInject x =>
                ("ramping_inject", x.Rate, null, $"ramp to {x.Rate}/{Short(x.Interval)}"),
            LoadSimulation.Inject x =>
                ("inject", x.Rate, null, $"inject {x.Rate}/{Short(x.Interval)}"),
            LoadSimulation.InjectRandom x =>
                ("inject_random", x.MaxRate, null, $"inject {x.MinRate}-{x.MaxRate}/{Short(x.Interval)}"),
            LoadSimulation.IterationsForConstant x =>
                ("iterations_for_constant", x.Copies, x.Iterations, $"{x.Iterations} iterations, {x.Copies} copies"),
            LoadSimulation.IterationsForInject x =>
                ("iterations_for_inject", x.Rate, x.Iterations, $"{x.Iterations} iterations at {x.Rate}/{Short(x.Interval)}"),
            LoadSimulation.Pause x =>
                ("pause", 0, null, $"pause {Short(x.During)}"),
            _ => ("unknown", 0, null, simulation.GetType().Name)
        };

    private static string Short(TimeSpan value) =>
        value.TotalSeconds < 60 ? $"{value.TotalSeconds:0.##}s" : $"{value:hh\\:mm\\:ss}";
}
