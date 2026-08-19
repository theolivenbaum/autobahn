# Autobahn benchmarks

Guards the paths where a regression would show up as the load generator measuring itself:

| Benchmark | Runs | What a regression here would mean |
|--|--|--|
| `MeasurementBenchmarks.PublishMeasurement` | once per step per iteration | The scenario's own thread pays it. Must not allocate. |
| `MeasurementBenchmarks.AccumulateMeasurement` | once per step per iteration | The stats actor's thread pays it. Falls behind under load if it grows. |
| `MeasurementBenchmarks.PublishAndBuildInterval` | once per reporting interval | Interval close blocks the live table. |
| `StatisticsBenchmarks.CreateScenarioStats` | once per scenario per interval | Grows with step count; the `[Params]` sweep shows how. |
| `SchedulerBenchmarks.*` | once per simulation interval per scenario | Cheap by construction; benchmarked so it stays that way. |
| `MetricsBenchmarks.Increment/Set/Record` | whenever a scenario writes a metric | Sits inside the user's iteration. Must not allocate. |
| `MetricsBenchmarks.SampleRuntimeMetrics` | twice a second, on its own timer | The collector must not become the thing it is measuring. |
| `MetricsBenchmarks.CloseInterval` | once per reporting interval | Over every metric at once; grows with how many there are. |

Not part of the root build. Run it explicitly, in Release:

```bash
dotnet run -c Release --project performance/Autobahn.Benchmarks -- --filter '*'
```

Narrow it while iterating:

```bash
dotnet run -c Release --project performance/Autobahn.Benchmarks -- --filter '*Measurement*'
```

Record the numbers before a change to the scheduler or the stats actor and compare after.
The C# engine should be at least as fast as what it replaced; where it is not, that is a
bug to fix rather than a cost to accept.

## Baseline

Taken on the commit that added this project, with `--job short` on a 4-core Linux
container. Absolute times will differ on your machine; the **Allocated** column should not.

| Method | Mean | Allocated |
|--|--:|--:|
| `PublishMeasurement` | 364 ns | **0 B** |
| `AccumulateMeasurement` | 16 ns | **0 B** |
| `PublishAndBuildInterval` (10,000 measurements) | 2.0 ms | 1.30 MB |
| `IncrementCounter` | 24 ns | **0 B** |
| `SetGauge` | 24 ns | **0 B** |
| `RecordHistogram` | 26 ns | **0 B** |
| `IncrementCounterFromEightThreads` | 78 ns | **0 B** |
| `SampleRuntimeMetrics` | 151 µs | 23 KB |
| `CloseInterval` (15 metrics) | 6.6 µs | 3.1 KB |

The per-request paths allocate nothing, which is the property worth defending: a generator
that allocates per request eventually reports its own GC pause as the target's latency. That
covers every metric write too - a counter, a gauge and a histogram all cost about what the
interlocked operation behind them costs, so a scenario can write one per iteration without
paying for it.

`PublishAndBuildInterval` allocates because closing an interval reads eight percentiles out
of two histograms per step and builds fresh stats records - that happens once per scenario
per reporting interval, not per request.

`SampleRuntimeMetrics` is the expensive one in absolute terms, and it is fine: it runs on its
own timer twice a second, so 151 µs is around 0.03% of one core. Most of the cost is the
process counters, which on Linux are a trip through `/proc`; the thread count is read at a
fraction of the sampling rate because enumerating every thread in the process costs more than
everything else here put together.
