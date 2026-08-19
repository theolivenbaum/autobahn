using System.Diagnostics;
using Autobahn.Stats;
using Autobahn.Ui.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Autobahn.Cli.Ui;

/// <summary>
/// Runs a load test with the live UI serving beside it.
/// </summary>
/// <remarks>
/// The connection between the two is one interval observer and one cancellation token, both
/// of which the engine already had. Nothing here is a hook the engine grew for a UI: the run
/// writes intervals into a feed the way it writes them into a report, and a browser reads the
/// feed. That is what makes the promise in TODO.md section 8 keepable - the run's timing,
/// results and exit code do not depend on who is watching.
/// </remarks>
internal sealed class UiSession : IAsyncDisposable
{
    private readonly RunFeed _feed;
    private readonly UiServer _server;
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly LogCapture _logs = new();

    private UiSession(RunFeed feed, UiServer server)
    {
        _feed = feed;
        _server = server;
    }

    public string Url => _server.Url;

    public static async Task<UiSession> StartAsync(UiOptions options, CancellationToken cancellationToken)
    {
        var feed = new RunFeed(options);
        var server = await UiServer.StartAsync(options, feed, cancellationToken).ConfigureAwait(false);

        var session = new UiSession(feed, server);
        feed.OnStopRequested = _ => session._stop.Cancel();

        return session;
    }

    /// <summary>
    /// Lays the UI over a run: the descriptor it reads, the frames it follows, the stop
    /// button it offers, and the log lines it tails.
    /// </summary>
    public AutobahnContext Attach(AutobahnContext context, IReadOnlyList<ScenarioProps> scenarios)
    {
        // Provisional, so a page opened while the run is still starting has something to draw.
        // The session-start observer below replaces it with what the engine actually resolved,
        // which is not the same thing: a JSON config or an environment variable can move the
        // reporting interval, the test name and the scenario list out from under this.
        _feed.Run = Describe(context, scenarios);

        return context
            .WithSessionStartObserver(start =>
            {
                _feed.Run = Describe(start);
                _feed.ReportFolder = start.ReportFolder;
                _feed.SessionId = start.TestInfo.SessionId;

                return Task.CompletedTask;
            })
            .WithIntervalObserver(record =>
            {
                _feed.Publish(FrameBuilder.Frame(
                    record,
                    RunState.Bombing,
                    $"{record.Duration:hh\\:mm\\:ss} elapsed",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    record.Thresholds,
                    _logs.Drain(_clock.Elapsed)));

                return Task.CompletedTask;
            })
            .WithCancellationToken(_stop.Token)
            // Added, not substituted: the run keeps writing its own log file while the UI
            // tails it.
            .WithLoggerProvider(_logs);
    }

    /// <summary>Publishes the run's last word, so a watching page ends on the truth.</summary>
    public void Complete(SessionStats stats)
    {
        _feed.SetReports(stats.ReportFiles
            .Select(x => new ReportDescriptor
            {
                FileName = Path.GetFileName(x.FilePath),
                Format = x.ReportFormat.ToString(),
                SizeBytes = x.ReportContent.Length
            })
            .ToArray());

        var state = stats.AllThresholdsPassed ? RunState.Finished : RunState.Failed;

        var verdict = stats.Thresholds.Length == 0
            ? "Finished."
            : stats.AllThresholdsPassed
                ? $"Finished. All {stats.Thresholds.Length} threshold(s) passed."
                : $"Finished. {stats.Thresholds.Count(x => !x.Passed)} of {stats.Thresholds.Length} threshold(s) failed.";

        _feed.Publish(new LiveFrame
        {
            ElapsedSeconds = stats.Duration.TotalSeconds,
            TimestampEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            State = state,
            StatusText = verdict,
            Scenarios = [],
            Metrics = [],
            Thresholds = stats.Thresholds
                .Select(x => new ThresholdFrame
                {
                    Name = x.Name,
                    ScenarioName = x.ScenarioName,
                    Observed = x.ObservedValue,
                    Passing = x.Passed,
                    Checked = x.TotalChecks > 0,
                    FailedChecks = x.FailedChecks,
                    TotalChecks = x.TotalChecks,
                    Aborted = x.Aborted
                })
                .ToArray(),
            Logs = _logs.Drain(_clock.Elapsed)
        });
    }

    /// <summary>The run as the engine resolved it, which is the version that is true.</summary>
    private static RunDescriptor Describe(SessionStartInfo start) => new()
    {
        SessionId = start.TestInfo.SessionId,
        TestSuite = start.TestInfo.TestSuite,
        TestName = start.TestInfo.TestName,
        StartedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ReportingIntervalSeconds = start.ReportingInterval.TotalSeconds,
        PlannedDurationSeconds = start.Scenarios.Any(x => x.PlannedDuration is null) || start.Scenarios.Count == 0
            ? null
            : start.Scenarios.Max(x => x.PlannedDuration!.Value.TotalSeconds),
        Host = new HostDescriptor
        {
            MachineName = start.HostInfo.MachineName,
            OperatingSystem = start.HostInfo.OS,
            Architecture = start.HostInfo.Processor,
            ProcessorCount = start.HostInfo.CoresCount,
            AutobahnVersion = start.HostInfo.AutobahnVersion
        },
        Scenarios = start.Scenarios
            .Select(scn => new ScenarioDescriptor
            {
                ScenarioName = scn.ScenarioName,
                PlannedDurationSeconds = scn.PlannedDuration?.TotalSeconds,
                WarmUpDurationSeconds = scn.WarmUpDuration?.TotalSeconds,
                MaxCopies = scn.MaxCopies,
                Weight = scn.Weight,
                Plan = FrameBuilder.Plan(scn.LoadSimulations)
            })
            .ToArray(),
        Settings = start.EffectiveSettings
            .Select(x => new SettingDescriptor { Name = x.Name, Value = x.Value, Source = x.Source.ToString() })
            .ToArray(),
        Thresholds = start.Thresholds.Select(FrameBuilder.Descriptor).ToArray()
    };

    private RunDescriptor Describe(AutobahnContext context, IReadOnlyList<ScenarioProps> scenarios) => new()
    {
        TestSuite = context.TestSuite,
        TestName = context.TestName,
        StartedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ReportingIntervalSeconds = context.Reporting.ReportingInterval.TotalSeconds,
        PlannedDurationSeconds = PlannedDuration(scenarios),
        Host = new HostDescriptor
        {
            MachineName = Environment.MachineName,
            OperatingSystem = Environment.OSVersion.VersionString,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            AutobahnVersion = typeof(AutobahnRunner).Assembly.GetName().Version?.ToString() ?? ""
        },
        Scenarios = scenarios
            .Select(scn => new ScenarioDescriptor
            {
                ScenarioName = scn.ScenarioName,
                PlannedDurationSeconds = Duration(scn)?.TotalSeconds,
                WarmUpDurationSeconds = scn.WarmUpDuration?.TotalSeconds,
                MaxCopies = scn.LoadSimulations.Count == 0 ? 0 : scn.LoadSimulations.Max(Level),
                Weight = scn.Weight,
                Plan = FrameBuilder.Plan(scn.LoadSimulations)
            })
            .ToArray(),
        Thresholds = context.Thresholds.Select(FrameBuilder.Descriptor).ToArray()
    };

    private static double? PlannedDuration(IReadOnlyList<ScenarioProps> scenarios)
    {
        var durations = scenarios.Select(Duration).ToArray();

        // A counted plan has no length to promise, and a progress bar that invents one is
        // worse than no progress bar.
        return durations.Any(x => x is null) || durations.Length == 0
            ? null
            : durations.Max(x => x!.Value.TotalSeconds);
    }

    private static TimeSpan? Duration(ScenarioProps scenario)
    {
        if (scenario.LoadSimulations.Count == 0) return TimeSpan.Zero;
        if (scenario.LoadSimulations.Any(x => x.IterationCount is not null)) return null;

        return scenario.LoadSimulations.Aggregate(TimeSpan.Zero, (total, x) => total + x.Duration);
    }

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

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
        _logs.Dispose();
    }
}

/// <summary>
/// Keeps the run's recent log lines so a watching page can tail them.
/// </summary>
/// <remarks>
/// Bounded and drained per frame: a run that logs heavily must not fill memory because a
/// browser is slow to read, and a tail nobody is reading is not worth keeping. Lines that
/// overflow are dropped rather than queued, for the same reason a slow client drops frames.
/// </remarks>
internal sealed class LogCapture : ILoggerProvider
{
    private const int Capacity = 500;

    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Level, string Message)> _lines = new();

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);

    public LogLine[] Drain(TimeSpan elapsed)
    {
        var lines = new List<LogLine>();

        while (_lines.TryDequeue(out var line))
        {
            lines.Add(new LogLine
            {
                ElapsedSeconds = elapsed.TotalSeconds,
                Level = line.Level,
                Message = line.Message
            });
        }

        return [.. lines];
    }

    private void Add(string level, string message)
    {
        _lines.Enqueue((level, message));

        while (_lines.Count > Capacity) _lines.TryDequeue(out _);
    }

    public void Dispose() { }

    private sealed class CaptureLogger(LogCapture capture) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            capture.Add(logLevel.ToString(), formatter(state, exception));
        }
    }
}
