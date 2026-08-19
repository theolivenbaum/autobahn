namespace Autobahn.Ui.Contracts;

/// <summary>The schema both ends agree on.</summary>
/// <remarks>
/// Two rules hold across every record in this project, and both come from the far end of the
/// wire being a browser rather than another .NET process.
///
/// <b>No <c>long</c>.</b> The transpiled BCL models a 64-bit integer as an object with its own
/// arithmetic, and JSON has no way to say which numbers are those - so a deserialized
/// <c>long</c> arrives as a plain JavaScript number and every method call on it fails at
/// runtime rather than at compile time. Counts and epoch milliseconds are <c>double</c>, which
/// is exact to 2^53 and therefore exact for both.
///
/// <b>No <c>DateTimeOffset</c>.</b> Times are milliseconds since the Unix epoch, because the
/// other end is JavaScript and <c>new Date(ms)</c> is the whole conversion.
///
/// <b>Every record declares its own parameterless constructor.</b> A record gets one anyway,
/// but the compiler marks it synthetic, and the browser-side deserializer skips synthetic
/// members - it then reports that the type has no default constructor and the whole document
/// fails to read. Written out it is an ordinary constructor, and the records stay records.
/// </remarks>
public static class UiSchema
{
    /// <summary>
    /// Bumped when a field is removed or its meaning changes; adding one does not bump it.
    /// </summary>
    /// <remarks>
    /// A browser tab left open across a rebuild is the normal case, not the edge, so both
    /// ends carry the version and the UI says "reload" rather than misreading a frame.
    /// </remarks>
    public const int Version = 1;
}

/// <summary>Where the run is in its own lifecycle.</summary>
public enum RunState
{
    Init = 0,
    WarmUp = 1,
    Bombing = 2,
    Stopping = 3,
    Finished = 4,
    Failed = 5
}

/// <summary>
/// One reporting interval, as it happened.
/// </summary>
/// <remarks>
/// Numbered, so a client that misses one can tell - a WebSocket that drops and reconnects
/// loses whatever was in flight, and a chart with an invisible hole in it is worse than one
/// that knows to backfill. <see cref="Sequence"/> is what <c>/api/history?from=</c> takes.
/// </remarks>
public sealed record LiveFrame
{
    // Declared rather than left implicit; see UiSchema.
    public LiveFrame() { }

    public int SchemaVersion { get; init; } = UiSchema.Version;

    /// <summary>Monotonic from 1. A gap means frames were missed.</summary>
    public double Sequence { get; init; }

    /// <summary>How far into the run this interval ends.</summary>
    public double ElapsedSeconds { get; init; }

    /// <summary>When this interval closed: milliseconds since the Unix epoch, UTC.</summary>
    public double TimestampEpochMs { get; init; }

    public RunState State { get; init; }

    /// <summary>What the run is doing right now, in words.</summary>
    public string StatusText { get; init; } = "";

    public ScenarioFrame[] Scenarios { get; init; } = [];
    public MetricFrame[] Metrics { get; init; } = [];
    public ThresholdFrame[] Thresholds { get; init; } = [];

    /// <summary>Log lines since the previous frame, oldest first.</summary>
    public LogLine[] Logs { get; init; } = [];
}

/// <summary>One scenario's numbers for one interval.</summary>
public sealed record ScenarioFrame
{
    // Declared rather than left implicit; see UiSchema.
    public ScenarioFrame() { }

    public string ScenarioName { get; init; } = "";

    /// <summary>The load simulation running right now, and its level.</summary>
    public string SimulationName { get; init; } = "";
    public int SimulationValue { get; init; }

    /// <summary>
    /// How many copies are actually live, against how many the plan asked for.
    /// </summary>
    /// <remarks>
    /// The two diverging is the clearest sign the generator is saturated rather than the
    /// target, which is why they travel together rather than being derived apart.
    /// </remarks>
    public int ScheduledCopies { get; init; }
    public int ActualCopies { get; init; }

    public MeasurementFrame Ok { get; init; } = new();
    public MeasurementFrame Fail { get; init; } = new();

    public StepFrame[] Steps { get; init; } = [];
    public StatusCodeFrame[] StatusCodes { get; init; } = [];
}

/// <summary>One step's numbers for one interval.</summary>
public sealed record StepFrame
{
    // Declared rather than left implicit; see UiSchema.
    public StepFrame() { }

    public string StepName { get; init; } = "";
    public MeasurementFrame Ok { get; init; } = new();
    public MeasurementFrame Fail { get; init; } = new();
}

/// <summary>One side - ok or fail - of a scenario or step.</summary>
public sealed record MeasurementFrame
{
    // Declared rather than left implicit; see UiSchema.
    public MeasurementFrame() { }

    public int Count { get; init; }
    public double Rps { get; init; }

    public double MinMs { get; init; }
    public double MeanMs { get; init; }
    public double MaxMs { get; init; }
    public double P50Ms { get; init; }
    public double P75Ms { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }

    public double Bytes { get; init; }
}

/// <summary>How often one status code came back in this interval.</summary>
public sealed record StatusCodeFrame
{
    // Declared rather than left implicit; see UiSchema.
    public StatusCodeFrame() { }

    public string StatusCode { get; init; } = "";
    public bool IsError { get; init; }
    public string Message { get; init; } = "";
    public int Count { get; init; }
}

/// <summary>One metric's value for this interval.</summary>
public sealed record MetricFrame
{
    // Declared rather than left implicit; see UiSchema.
    public MetricFrame() { }

    public string Name { get; init; } = "";

    /// <summary>counter, gauge or histogram.</summary>
    public string Kind { get; init; } = "";

    public string Unit { get; init; } = "";

    public double Current { get; init; }
    public double Min { get; init; }
    public double Mean { get; init; }
    public double Max { get; init; }
    public double P50 { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
    public double WriteCount { get; init; }
}

/// <summary>One threshold's state as of this interval.</summary>
public sealed record ThresholdFrame
{
    // Declared rather than left implicit; see UiSchema.
    public ThresholdFrame() { }

    public string Name { get; init; } = "";
    public string ScenarioName { get; init; } = "";

    public double Observed { get; init; }
    public bool Passing { get; init; }

    /// <summary>False while the rule has not started checking, or has nothing to read.</summary>
    public bool Checked { get; init; }

    public int FailedChecks { get; init; }
    public int TotalChecks { get; init; }
    public bool Aborted { get; init; }
}

/// <summary>One line of the run's log.</summary>
public sealed record LogLine
{
    // Declared rather than left implicit; see UiSchema.
    public LogLine() { }

    public double ElapsedSeconds { get; init; }

    /// <summary>Trace, Debug, Information, Warning, Error or Critical.</summary>
    public string Level { get; init; } = "";

    public string Message { get; init; } = "";
}
