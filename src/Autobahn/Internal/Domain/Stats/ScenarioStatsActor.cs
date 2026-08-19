using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ZLogger;
using Autobahn.Stats;

namespace Autobahn.Internal.Domain.Stats;

/// <summary>
/// Owns all mutable statistics state for one scenario.
/// </summary>
/// <remarks>
/// Everything that mutates state arrives through an unbounded channel and is applied by a
/// single background loop, so nothing on the measurement path takes a lock. The mailbox
/// message is a struct, so publishing a measurement allocates nothing.
///
/// Two accumulators run side by side: an interval set, reset every reporting interval and
/// used for the live console, and a global set kept for the whole run and used for the
/// final report.
/// </remarks>
internal sealed class ScenarioStatsActor : IAsyncDisposable
{
    private enum MessageKind
    {
        AddMeasurement,
        BuildReportingStats,
        GetFinalStats
    }

    private readonly record struct ActorMessage(
        MessageKind Kind,
        Measurement Measurement,
        TaskCompletionSource<ScenarioStats>? Reply,
        LoadSimulationStats? SimulationStats,
        TimeSpan ExecutedDuration,
        TimeSpan Pause);

    private readonly ILogger _logger;
    private readonly RuntimeScenario _scenario;
    private readonly TimeSpan _reportingInterval;
    private readonly Channel<ActorMessage> _mailbox;
    private readonly Task _loop;

    private readonly Dictionary<string, int> _stepsOrder = new();
    private readonly List<long> _globalInfoDataSize = [];
    private readonly ConcurrentDictionary<TimeSpan, ScenarioStats> _reportingStatsCache = new();
    private readonly Dictionary<string, RawMeasurementStats> _globalStepsResults = new();
    private readonly Dictionary<string, RawMeasurementStats> _intervalStepsResults = new();
    private readonly List<Measurement> _tempBuffer = [];

    private TimeSpan _acceptMaxTimeBucket;
    private int _scenarioFailCount;
    private ScenarioStats _consoleScenarioStats;
    private bool _useTempBuffer = true;

    public ScenarioStatsActor(ILogger logger, RuntimeScenario scenario, TimeSpan reportingInterval)
    {
        _logger = logger;
        _scenario = scenario;
        _reportingInterval = reportingInterval;
        _acceptMaxTimeBucket = reportingInterval;

        var emptyScnStats = Statistics.EmptyScenarioStats(scenario);
        var globalInfoStep = Statistics.ExtractGlobalInfoStep(emptyScnStats);
        _consoleScenarioStats = emptyScnStats with { StepStats = [globalInfoStep, .. emptyScnStats.StepStats] };

        _stepsOrder[Constants.ScenarioGlobalInfo] = 0;

        _mailbox = Channel.CreateUnbounded<ActorMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        });

        _loop = Task.Run(RunLoopAsync);
    }

    /// <summary>How many scenario iterations have failed. Read by the scheduler to enforce MaxFailCount.</summary>
    public int ScenarioFailCount => Volatile.Read(ref _scenarioFailCount);

    public IReadOnlyDictionary<TimeSpan, ScenarioStats> AllRealtimeStats => _reportingStatsCache;

    /// <summary>Absolute counts since the run started, for the live console table.</summary>
    public ScenarioStats ConsoleScenarioStats => Volatile.Read(ref _consoleScenarioStats);

    public void AddMeasurement(in Measurement measurement) =>
        _mailbox.Writer.TryWrite(new ActorMessage(MessageKind.AddMeasurement, measurement, null, null, TimeSpan.Zero, TimeSpan.Zero));

    /// <summary>
    /// Closes the current reporting interval and returns its stats.
    /// </summary>
    /// <param name="simulationStats">The load simulation the interval ran under.</param>
    /// <param name="executedDuration">How long the scenario has actually been running.</param>
    /// <param name="pause">
    /// How much of the interval was spent in a pause simulation. Deducted from the window
    /// throughput is computed over, so an interval that was half paused does not report half
    /// the rate the scenario actually achieved.
    /// </param>
    public Task<ScenarioStats> BuildReportingStats(
        LoadSimulationStats simulationStats, TimeSpan executedDuration, TimeSpan pause = default)
    {
        var reply = new TaskCompletionSource<ScenarioStats>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_mailbox.Writer.TryWrite(new ActorMessage(
                MessageKind.BuildReportingStats, default, reply, simulationStats, executedDuration, pause)))
        {
            reply.TrySetResult(Statistics.EmptyScenarioStats(_scenario));
        }

        return reply.Task;
    }

    /// <summary>Builds the run's final stats from the global accumulator.</summary>
    public Task<ScenarioStats> GetFinalStats(LoadSimulationStats simulationStats, TimeSpan executedDuration, TimeSpan pause)
    {
        var reply = new TaskCompletionSource<ScenarioStats>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_mailbox.Writer.TryWrite(new ActorMessage(
                MessageKind.GetFinalStats, default, reply, simulationStats, executedDuration, pause)))
        {
            reply.TrySetResult(Statistics.EmptyScenarioStats(_scenario));
        }

        return reply.Task;
    }

    private async Task RunLoopAsync()
    {
        try
        {
            await foreach (var msg in _mailbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                switch (msg.Kind)
                {
                    case MessageKind.AddMeasurement:
                        AddMeasurementCore(msg.Measurement);
                        break;

                    case MessageKind.BuildReportingStats:
                    {
                        var stats = BuildStats(
                            [.. _intervalStepsResults.Values], msg.SimulationStats!,
                            msg.ExecutedDuration, msg.Pause, isFinalStats: false);

                        AddReportingStats(stats);
                        FlushTempBuffer();
                        msg.Reply!.TrySetResult(stats);
                        break;
                    }

                    case MessageKind.GetFinalStats:
                    {
                        FlushTempBuffer();

                        var stats = BuildStats(
                            [.. _globalStepsResults.Values], msg.SimulationStats!,
                            msg.ExecutedDuration, msg.Pause, isFinalStats: true);

                        msg.Reply!.TrySetResult(stats);
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.ZLogCritical($"Unhandled exception: {nameof(ScenarioStatsActor)} failed: {ex}");
        }
    }

    private void AddMeasurementCore(in Measurement measurement)
    {
        // Measurements belonging to a later interval wait in the buffer so they land in the
        // interval they were started in rather than the one that happens to be open now.
        if (_useTempBuffer && measurement.CurrentTimeBucket >= _acceptMaxTimeBucket)
        {
            _tempBuffer.Add(measurement);
            return;
        }

        UpdateGlobalInfoDataSize(measurement);
        var finalDataSize = CalcFinalDataSize(measurement);

        if (!_globalStepsResults.TryGetValue(measurement.Name, out var globalStats))
        {
            globalStats = RawMeasurementStats.Empty(measurement.Name);
            _globalStepsResults[measurement.Name] = globalStats;

            _stepsOrder.TryAdd(measurement.Name, _stepsOrder.Count);
        }

        globalStats.AddMeasurement(measurement, finalDataSize);

        if (!_intervalStepsResults.TryGetValue(measurement.Name, out var intervalStats))
        {
            intervalStats = RawMeasurementStats.Empty(measurement.Name);
            _intervalStepsResults[measurement.Name] = intervalStats;
        }

        intervalStats.AddMeasurement(measurement, finalDataSize);

        if (measurement.ClientResponse.IsError && measurement.Name == Constants.ScenarioGlobalInfo)
            Volatile.Write(ref _scenarioFailCount, _scenarioFailCount + 1);
    }

    /// <summary>Remembers the bytes a step reported, so the scenario row can account for them once.</summary>
    private void UpdateGlobalInfoDataSize(in Measurement measurement)
    {
        if (measurement.Name != Constants.ScenarioGlobalInfo && measurement.ClientResponse.SizeBytes > 0)
            _globalInfoDataSize.Add(measurement.ClientResponse.SizeBytes);
    }

    /// <summary>The scenario row's data size is its own bytes plus everything its steps transferred.</summary>
    private long CalcFinalDataSize(in Measurement measurement)
    {
        if (measurement.Name != Constants.ScenarioGlobalInfo || _globalInfoDataSize.Count == 0)
            return measurement.ClientResponse.SizeBytes;

        var sizeBytes = _globalInfoDataSize.Sum() + measurement.ClientResponse.SizeBytes;
        _globalInfoDataSize.Clear();
        return sizeBytes;
    }

    private void FlushTempBuffer()
    {
        _useTempBuffer = false;

        foreach (var measurement in _tempBuffer)
            AddMeasurementCore(measurement);

        _tempBuffer.Clear();
        _useTempBuffer = true;
    }

    private ScenarioStats BuildStats(
        RawMeasurementStats[] rawStats,
        LoadSimulationStats simulationStats,
        TimeSpan executedDuration,
        TimeSpan pause,
        bool isFinalStats)
    {
        // Steps are reported in the order they first appeared, so a report diff is a diff
        // of numbers rather than of row order.
        Array.Sort(rawStats, (a, b) => _stepsOrder[a.Name].CompareTo(_stepsOrder[b.Name]));

        return isFinalStats
            ? Statistics.CreateScenarioStats(
                _scenario.ScenarioName, rawStats, simulationStats, OperationType.Complete,
                executedDuration, executedDuration, pause)
            : Statistics.CreateScenarioStats(
                _scenario.ScenarioName, rawStats, simulationStats, OperationType.Bombing,
                executedDuration, _reportingInterval, pause);
    }

    private void AddReportingStats(ScenarioStats reportingStats)
    {
        _reportingStatsCache[reportingStats.Duration] = reportingStats;
        Volatile.Write(ref _consoleScenarioStats, MergeConsoleStats(ConsoleScenarioStats, reportingStats));

        _intervalStepsResults.Clear();
        _acceptMaxTimeBucket += _reportingInterval;
    }

    /// <summary>
    /// The live table shows absolute counts, so each interval's counts are added to what the
    /// console already displayed. Latencies are left as the interval's own.
    /// </summary>
    private static ScenarioStats MergeConsoleStats(ScenarioStats consoleStats, ScenarioStats reportingStats)
    {
        var globalInfoStep = Statistics.ExtractGlobalInfoStep(reportingStats);

        var updatedSteps = new[] { globalInfoStep }
            .Concat(reportingStats.StepStats)
            .Select(newStepStats =>
            {
                var console = consoleStats.StepStats.FirstOrDefault(x => x.StepName == newStepStats.StepName);
                if (console is null) return newStepStats;

                var ok = newStepStats.Ok with
                {
                    Request = newStepStats.Ok.Request with
                    {
                        Count = newStepStats.Ok.Request.Count + console.Ok.Request.Count
                    },
                    DataTransfer = newStepStats.Ok.DataTransfer with
                    {
                        AllBytes = newStepStats.Ok.DataTransfer.AllBytes + console.Ok.DataTransfer.AllBytes
                    }
                };

                var fail = newStepStats.Fail with
                {
                    Request = newStepStats.Fail.Request with
                    {
                        Count = newStepStats.Fail.Request.Count + console.Fail.Request.Count
                    },
                    DataTransfer = newStepStats.Fail.DataTransfer with
                    {
                        AllBytes = newStepStats.Fail.DataTransfer.AllBytes + console.Fail.DataTransfer.AllBytes
                    }
                };

                return newStepStats with { Ok = ok, Fail = fail };
            })
            .ToArray();

        return reportingStats with { StepStats = updatedSteps };
    }

    public async ValueTask DisposeAsync()
    {
        _mailbox.Writer.TryComplete();

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.ZLogWarning($"{nameof(ScenarioStatsActor)} did not shut down cleanly: {ex.Message}");
        }
    }
}
