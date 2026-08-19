using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Autobahn.Internal.Domain.Metrics;
using Autobahn.Metrics;

namespace Autobahn.Benchmarks;

/// <summary>
/// The metric write path: what a scenario pays for tracking something of its own.
/// </summary>
/// <remarks>
/// The claim these guard is that a metric write costs about as much as the interlocked
/// operation behind it and allocates nothing, so writing one from inside an iteration does
/// not show up as the target's latency. The runtime collector is here too: it runs on a
/// timer rather than on the hot path, but a sample that took milliseconds would still steal
/// them from the generator.
/// </remarks>
[MemoryDiagnoser]
public class MetricsBenchmarks
{
    private const int WriteCount = 10_000;

    private MetricRegistry _registry = null!;
    private ICounter _counter = null!;
    private IGauge _gauge = null!;
    private IHistogram _histogram = null!;
    private RuntimeMetrics _runtime = null!;

    [GlobalSetup]
    public void Setup()
    {
        _registry = new MetricRegistry();
        _counter = _registry.Counter("bench.counter");
        _gauge = _registry.Gauge("bench.gauge");
        _histogram = _registry.Histogram("bench.histogram");
        _runtime = new RuntimeMetrics(_registry, NullLogger.Instance, TimeProvider.System);
    }

    [GlobalCleanup]
    public void Cleanup() => _runtime.Dispose();

    [Benchmark(OperationsPerInvoke = WriteCount)]
    public void IncrementCounter()
    {
        for (var i = 0; i < WriteCount; i++) _counter.Increment();
    }

    [Benchmark(OperationsPerInvoke = WriteCount)]
    public void SetGauge()
    {
        for (var i = 0; i < WriteCount; i++) _gauge.Set(i);
    }

    [Benchmark(OperationsPerInvoke = WriteCount)]
    public void RecordHistogram()
    {
        for (var i = 0; i < WriteCount; i++) _histogram.Record(i % 500);
    }

    /// <summary>Contended writes: what a counter costs with every copy of a scenario on it.</summary>
    [Benchmark(OperationsPerInvoke = WriteCount)]
    public void IncrementCounterFromEightThreads()
    {
        const int threads = 8;
        const int perThread = WriteCount / threads;

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++) _counter.Increment();
        });
    }

    /// <summary>One runtime sample: what the collector's timer costs each time it fires.</summary>
    [Benchmark]
    public void SampleRuntimeMetrics() => _runtime.Sample();

    /// <summary>Closing the interval: paid once per reporting interval, over every metric.</summary>
    [Benchmark]
    public int CloseInterval() => _registry.CloseInterval().Length;
}
