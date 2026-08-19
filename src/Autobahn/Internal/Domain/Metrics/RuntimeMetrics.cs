using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ZLogger;
using Autobahn.Metrics;

namespace Autobahn.Internal.Domain.Metrics;

/// <summary>
/// Samples the load generator's own health while the run is in flight: CPU, memory, GC,
/// thread pool and socket traffic.
/// </summary>
/// <remarks>
/// This is what turns "the target got slower" into "the load generator ran out of thread
/// pool". A load test that cannot show it was not itself the bottleneck is not evidence, so
/// these are collected whether or not the user asked for them.
///
/// Sampling runs on its own timer, deliberately faster than the reporting interval: a
/// five-second reporting interval that sampled once would report one instant rather than
/// the window. Every counter is read behind its own try/catch and dropped for the rest of
/// the run if the platform does not have it - a missing counter is not worth failing a run
/// that is otherwise producing numbers.
/// </remarks>
internal sealed class RuntimeMetrics : IDisposable
{
    private readonly ILogger _logger;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _cpuTimer = Stopwatch.StartNew();
    private readonly SocketTraffic? _sockets;

    private readonly IGauge _cpu;
    private readonly IGauge _workingSet;
    private readonly IGauge _gcHeap;
    private readonly IGauge _threadPoolQueue;
    private readonly IGauge _threadPoolThreads;
    private readonly IGauge _threads;
    private readonly ICounter _gen0;
    private readonly ICounter _gen1;
    private readonly ICounter _gen2;
    private readonly ICounter? _bytesSent;
    private readonly ICounter? _bytesReceived;

    private TimeSpan _lastCpuTime;
    private TimeSpan _lastCpuAt;
    private int _sampleNumber;
    private int _lastGen0, _lastGen1, _lastGen2;
    private long _lastSent, _lastReceived;

    private Timer? _timer;
    private bool _cpuUnavailable;
    private bool _processUnavailable;

    public RuntimeMetrics(MetricRegistry registry, ILogger logger)
    {
        _logger = logger;

        _cpu = registry.Gauge(Constants.MetricCpuPercent, MetricUnit.Percent);
        _workingSet = registry.Gauge(Constants.MetricWorkingSet, MetricUnit.Megabytes);
        _gcHeap = registry.Gauge(Constants.MetricGcHeap, MetricUnit.Megabytes);
        _threadPoolQueue = registry.Gauge(Constants.MetricThreadPoolQueue, MetricUnit.Count);
        _threadPoolThreads = registry.Gauge(Constants.MetricThreadPoolThreads, MetricUnit.Count);
        _threads = registry.Gauge(Constants.MetricThreads, MetricUnit.Count);
        _gen0 = registry.Counter(Constants.MetricGen0Collections);
        _gen1 = registry.Counter(Constants.MetricGen1Collections);
        _gen2 = registry.Counter(Constants.MetricGen2Collections);

        _sockets = SocketTraffic.TryStart(logger);

        if (_sockets is not null)
        {
            _bytesSent = registry.Counter(Constants.MetricSocketBytesSent, MetricUnit.Megabytes);
            _bytesReceived = registry.Counter(Constants.MetricSocketBytesReceived, MetricUnit.Megabytes);
        }

        _lastCpuTime = SafeCpuTime() ?? TimeSpan.Zero;
        _lastCpuAt = _cpuTimer.Elapsed;
        _lastGen0 = GC.CollectionCount(0);
        _lastGen1 = GC.CollectionCount(1);
        _lastGen2 = GC.CollectionCount(2);
    }

    public void Start(TimeSpan sampleInterval) =>
        _timer = new Timer(_ => Sample(), null, sampleInterval, sampleInterval);

    /// <summary>Takes one sample. Public so the final report is not missing the last window.</summary>
    public void Sample()
    {
        try
        {
            // One refresh per sample. Every process counter below reads the snapshot it takes,
            // and on Linux each refresh is a trip through /proc.
            RefreshProcess();

            _sampleNumber++;

            SampleCpu();
            SampleMemory();
            SampleGc();
            SampleThreadPool();
            SampleSockets();
        }
        catch (Exception ex)
        {
            // A broken counter must not take the run with it.
            _logger.ZLogDebug($"Runtime metrics sampling failed: {ex.Message}");
        }
    }

    private void RefreshProcess()
    {
        if (_processUnavailable) return;

        try
        {
            _process.Refresh();
        }
        catch (Exception ex)
        {
            _processUnavailable = true;
            _logger.ZLogDebug($"Process counters are unavailable on this platform: {ex.Message}");
        }
    }

    private void SampleCpu()
    {
        if (_cpuUnavailable) return;

        var cpuTime = SafeCpuTime();
        if (cpuTime is not { } total) return;

        var now = _cpuTimer.Elapsed;
        var wallClock = now - _lastCpuAt;

        if (wallClock <= TimeSpan.Zero) return;

        var used = total - _lastCpuTime;
        _lastCpuTime = total;
        _lastCpuAt = now;

        // Across all cores, so a fully loaded 8-core box reads 100 rather than 800.
        var percent = used.TotalMilliseconds / (wallClock.TotalMilliseconds * Environment.ProcessorCount) * 100.0;
        _cpu.Set(Math.Clamp(percent, 0.0, 100.0));
    }

    private void SampleMemory()
    {
        _gcHeap.Set(GC.GetTotalMemory(forceFullCollection: false));

        if (_processUnavailable) return;

        try
        {
            _workingSet.Set(_process.WorkingSet64);

            // Process.Threads builds a ProcessThread for every thread in the process, which
            // is by far the most expensive counter here. The number moves slowly, so it is
            // read at a fraction of the sampling rate rather than dropped.
            if (_sampleNumber % Constants.ProcessThreadSampleEvery == 1)
                _threads.Set(_process.Threads.Count);
        }
        catch (Exception ex)
        {
            _processUnavailable = true;
            _logger.ZLogDebug($"Process counters are unavailable on this platform: {ex.Message}");
        }
    }

    private void SampleGc()
    {
        // Collection counts are cumulative; the metric wants what happened since last time.
        AddDelta(_gen0, ref _lastGen0, GC.CollectionCount(0));
        AddDelta(_gen1, ref _lastGen1, GC.CollectionCount(1));
        AddDelta(_gen2, ref _lastGen2, GC.CollectionCount(2));

        static void AddDelta(ICounter counter, ref int last, int current)
        {
            var delta = current - last;
            last = current;
            if (delta > 0) counter.Add(delta);
        }
    }

    private void SampleThreadPool()
    {
        _threadPoolQueue.Set(ThreadPool.PendingWorkItemCount);
        _threadPoolThreads.Set(ThreadPool.ThreadCount);
    }

    private void SampleSockets()
    {
        if (_sockets is null || _bytesSent is null || _bytesReceived is null) return;

        var sent = _sockets.BytesSent;
        var received = _sockets.BytesReceived;

        if (sent > _lastSent) _bytesSent.Add(sent - _lastSent);
        if (received > _lastReceived) _bytesReceived.Add(received - _lastReceived);

        _lastSent = sent;
        _lastReceived = received;
    }

    private TimeSpan? SafeCpuTime()
    {
        try
        {
            return _process.TotalProcessorTime;
        }
        catch (Exception ex)
        {
            _cpuUnavailable = true;
            _logger.ZLogDebug($"CPU time is unavailable on this platform: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _sockets?.Dispose();
        _process.Dispose();
    }
}
