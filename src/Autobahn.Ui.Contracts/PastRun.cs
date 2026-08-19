namespace Autobahn.Ui.Contracts;

/// <summary>
/// A run that already finished, read back from its run artifact.
/// </summary>
/// <remarks>
/// The list this appears in is what turns Autobahn from "how fast is it" into "did this
/// commit make it slower", so it is deliberately cheap: enough to choose from, with the
/// per-scenario detail fetched only for the two runs actually being compared.
/// </remarks>
public sealed record PastRunSummary
{
    // Declared rather than left implicit; see UiSchema.
    public PastRunSummary() { }

    /// <summary>The folder the artifact was found in, which is also how it is fetched.</summary>
    public string Id { get; init; } = "";

    public string TestSuite { get; init; } = "";
    public string TestName { get; init; } = "";

    /// <summary>When the run finished: milliseconds since the Unix epoch, UTC.</summary>
    public double CompletedAtEpochMs { get; init; }

    public double DurationSeconds { get; init; }

    public int Ok { get; init; }
    public int Fail { get; init; }
    public double Rps { get; init; }
    public double P95Ms { get; init; }

    /// <summary>True for the run this page is watching, which is not comparable to itself.</summary>
    public bool IsCurrent { get; init; }

    /// <summary>Whether every threshold the run declared passed. Null when it declared none.</summary>
    public bool? ThresholdsPassed { get; init; }
}

/// <summary>One past run in full, as much of it as a comparison needs.</summary>
public sealed record PastRunDetail
{
    // Declared rather than left implicit; see UiSchema.
    public PastRunDetail() { }

    public int SchemaVersion { get; init; } = UiSchema.Version;

    public PastRunSummary Summary { get; init; } = new();

    public PastScenario[] Scenarios { get; init; } = [];
}

/// <summary>One scenario's totals in a past run.</summary>
public sealed record PastScenario
{
    // Declared rather than left implicit; see UiSchema.
    public PastScenario() { }

    public string ScenarioName { get; init; } = "";

    public MeasurementFrame Ok { get; init; } = new();
    public MeasurementFrame Fail { get; init; } = new();

    public PastStep[] Steps { get; init; } = [];
}

/// <summary>One step's totals in a past run.</summary>
public sealed record PastStep
{
    // Declared rather than left implicit; see UiSchema.
    public PastStep() { }

    public string StepName { get; init; } = "";

    public MeasurementFrame Ok { get; init; } = new();
    public MeasurementFrame Fail { get; init; } = new();
}
