namespace Autobahn.Ui.Contracts;

/// <summary>
/// Everything a page needs to render immediately, however late it arrived.
/// </summary>
/// <remarks>
/// A run is watched from a tab opened at minute forty as often as from one opened at the
/// start, and a dashboard that begins empty and fills up over the next five minutes is not
/// watching anything. So the snapshot carries the run's shape, its history so far, and where
/// it is now - one request, and the page is current.
/// </remarks>
public sealed record RunSnapshot
{
    // Declared rather than left implicit; see UiSchema.
    public RunSnapshot() { }

    public int SchemaVersion { get; init; } = UiSchema.Version;

    public RunDescriptor Run { get; init; } = new();

    /// <summary>The most recent frame, or null before the first interval closes.</summary>
    public LiveFrame? Latest { get; init; }

    /// <summary>
    /// The intervals so far, oldest first, possibly downsampled.
    /// </summary>
    /// <remarks>
    /// Downsampled by the host rather than the browser: at a five-second interval an hour is
    /// 720 points a series, which is fine, and hour six is not.
    /// </remarks>
    public LiveFrame[] History { get; init; } = [];

    /// <summary>True when <see cref="History"/> is thinned rather than every interval.</summary>
    public bool HistoryDownsampled { get; init; }

    /// <summary>The reports written so far.</summary>
    public ReportDescriptor[] Reports { get; init; } = [];
}

/// <summary>Backfill for a client that missed frames.</summary>
public sealed record HistoryResponse
{
    // Declared rather than left implicit; see UiSchema.
    public HistoryResponse() { }

    public int SchemaVersion { get; init; } = UiSchema.Version;

    public LiveFrame[] Frames { get; init; } = [];

    /// <summary>
    /// The oldest sequence the host still has. A client asking for less than this has fallen
    /// off the back of the buffer and should take a fresh snapshot instead of stitching.
    /// </summary>
    public double OldestSequence { get; init; }

    public bool Downsampled { get; init; }
}

/// <summary>One artifact the run produced.</summary>
public sealed record ReportDescriptor
{
    // Declared rather than left implicit; see UiSchema.
    public ReportDescriptor() { }

    public string FileName { get; init; } = "";

    /// <summary>Txt, Html, Csv, Md or Json.</summary>
    public string Format { get; init; } = "";

    public double SizeBytes { get; init; }
}

/// <summary>What a control request asked for, and what happened.</summary>
public sealed record ControlResult
{
    // Declared rather than left implicit; see UiSchema.
    public ControlResult() { }

    public bool Accepted { get; init; }
    public string Message { get; init; } = "";
    public RunState State { get; init; }
}
