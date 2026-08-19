# Autobahn benchmarks

Guards the paths where a regression would show up as the load generator measuring itself:

| Benchmark | Runs | What a regression here would mean |
|--|--|--|
| `MeasurementBenchmarks.PublishMeasurement` | once per step per iteration | The scenario's own thread pays it. Must not allocate. |
| `MeasurementBenchmarks.AccumulateMeasurement` | once per step per iteration | The stats actor's thread pays it. Falls behind under load if it grows. |
| `MeasurementBenchmarks.PublishAndBuildInterval` | once per reporting interval | Interval close blocks the live table. |
| `StatisticsBenchmarks.CreateScenarioStats` | once per scenario per interval | Grows with step count; the `[Params]` sweep shows how. |
| `SchedulerBenchmarks.*` | once per simulation interval per scenario | Cheap by construction; benchmarked so it stays that way. |

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

The two per-request paths allocate nothing, which is the property worth defending: a
generator that allocates per request eventually reports its own GC pause as the target's
latency. `PublishAndBuildInterval` allocates because closing an interval reads eight
percentiles out of two histograms per step and builds fresh stats records - that happens
once per scenario per reporting interval, not per request.
