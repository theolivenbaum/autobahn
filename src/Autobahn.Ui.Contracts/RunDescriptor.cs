namespace Autobahn.Ui.Contracts;

/// <summary>
/// What the run is: the things that are true for its whole life and do not arrive in frames.
/// </summary>
/// <remarks>
/// Fetched once when the page loads. Everything that changes as the run proceeds is a
/// <see cref="LiveFrame"/>, and the split is deliberate: a client that reconnects re-reads
/// this and then resumes the frame stream, rather than re-deriving the run's shape from
/// whatever frame happened to arrive first.
/// </remarks>
public sealed record RunDescriptor
{
    // Declared rather than left implicit; see UiSchema.
    public RunDescriptor() { }

    /// <summary>The schema this document follows, so a stale page can say so rather than misread.</summary>
    public int SchemaVersion { get; init; } = UiSchema.Version;

    public string SessionId { get; init; } = "";
    public string TestSuite { get; init; } = "";
    public string TestName { get; init; } = "";

    /// <summary>
    /// When the run started: milliseconds since the Unix epoch, UTC.
    /// </summary>
    /// <remarks>
    /// A number rather than a <c>DateTimeOffset</c>, because the transpiled BCL the UI
    /// compiles against does not have one - and because the other end of this wire is
    /// JavaScript, where <c>new Date(ms)</c> is the whole conversion.
    /// </remarks>
    public double StartedAtEpochMs { get; init; }

    /// <summary>How long the plan says it will take, or null when a counted plan cannot say.</summary>
    public double? PlannedDurationSeconds { get; init; }

    public double ReportingIntervalSeconds { get; init; }

    public HostDescriptor Host { get; init; } = new();

    public ScenarioDescriptor[] Scenarios { get; init; } = [];

    /// <summary>Each effective setting and the layer it came from.</summary>
    public SettingDescriptor[] Settings { get; init; } = [];

    /// <summary>The pass/fail rules this run is gated on, before any of them has been checked.</summary>
    public ThresholdDescriptor[] Thresholds { get; init; } = [];
}

/// <summary>The machine generating the load.</summary>
public sealed record HostDescriptor
{
    // Declared rather than left implicit; see UiSchema.
    public HostDescriptor() { }

    public string MachineName { get; init; } = "";
    public string OperatingSystem { get; init; } = "";
    public string Architecture { get; init; } = "";
    public int ProcessorCount { get; init; }
    public string AutobahnVersion { get; init; } = "";
}

/// <summary>One scenario and the plan it will run.</summary>
public sealed record ScenarioDescriptor
{
    // Declared rather than left implicit; see UiSchema.
    public ScenarioDescriptor() { }

    public string ScenarioName { get; init; } = "";

    /// <summary>How long this scenario's plan lasts, or null when it is counted in iterations.</summary>
    public double? PlannedDurationSeconds { get; init; }

    /// <summary>The most copies the plan ever runs at once.</summary>
    public int MaxCopies { get; init; }

    /// <summary>This scenario's share of the combined load, or null when it has none.</summary>
    public int? Weight { get; init; }

    /// <summary>
    /// How long this scenario warms up for before it is measured, or null when it does not.
    /// </summary>
    /// <remarks>
    /// Carried so the page can say "warming up" before the first interval arrives. Warm-up
    /// produces no frames by design - it is not part of the run's numbers - and a dashboard
    /// that showed nothing at all for thirty seconds would look broken rather than busy.
    /// </remarks>
    public double? WarmUpDurationSeconds { get; init; }

    /// <summary>The load plan as labelled segments, in order, for the plan timeline.</summary>
    public SimulationSegment[] Plan { get; init; } = [];
}

/// <summary>One segment of a load plan, laid out on a timeline.</summary>
public sealed record SimulationSegment
{
    // Declared rather than left implicit; see UiSchema.
    public SimulationSegment() { }

    /// <summary>The simulation's own name, e.g. <c>ramping_inject</c>.</summary>
    public string Kind { get; init; } = "";

    /// <summary>How far into the scenario this segment starts.</summary>
    public double StartSeconds { get; init; }

    /// <summary>How long it lasts, or null when it is counted in iterations rather than time.</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>Copies for a closed model, rate for an open one. Zero for a pause.</summary>
    public int Level { get; init; }

    /// <summary>Where the segment starts from, so a ramp can be drawn as a ramp.</summary>
    public int FromLevel { get; init; }

    /// <summary>How many iterations this segment runs, when it is counted rather than timed.</summary>
    public int? Iterations { get; init; }

    /// <summary>What to write on the segment.</summary>
    public string Label { get; init; } = "";
}

/// <summary>One effective setting and where its value came from.</summary>
public sealed record SettingDescriptor
{
    // Declared rather than left implicit; see UiSchema.
    public SettingDescriptor() { }

    public string Name { get; init; } = "";
    public string Value { get; init; } = "";

    /// <summary>Default, Code, JsonConfig, Environment or CommandLine.</summary>
    public string Source { get; init; } = "";
}

/// <summary>A pass/fail rule, as declared.</summary>
public sealed record ThresholdDescriptor
{
    // Declared rather than left implicit; see UiSchema.
    public ThresholdDescriptor() { }

    public string Name { get; init; } = "";
    public string Scope { get; init; } = "";
    public string Subject { get; init; } = "";

    /// <summary>The comparison as a symbol: <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>.</summary>
    public string Comparison { get; init; } = "";

    public double Target { get; init; }

    /// <summary>The scenario it is about, or empty when it is not scenario-scoped.</summary>
    public string ScenarioName { get; init; } = "";

    /// <summary>How far into the run it starts checking, or null when it starts at once.</summary>
    public double? StartsAfterSeconds { get; init; }

    /// <summary>How many consecutive failures end the run, or null when it is advisory.</summary>
    public int? AbortAfter { get; init; }
}
