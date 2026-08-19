<p align="center">
  <img src="assets/autobahn-logo.png" width="180" alt="Autobahn" />
</p>

# Autobahn

**Autobahn** is a load-testing library for .NET 10, written in pure C#. You write your
load test as ordinary C# — no DSL to learn — and Autobahn runs it, schedules the load,
measures every step, and reports what happened.

It is protocol-agnostic (HTTP, WebSockets, gRPC, AMQP, MQTT, SQL, Redis, anything you can
call from .NET) and model-agnostic (pull or push). If you can write the call, you can load
test it.

> Autobahn is a hard fork of [NBomber](https://github.com/PragmaticFlow/NBomber) at
> version **4.1.2**, the last release published under the Apache-2.0 license. All credit
> for the original design and implementation goes to Anton Moldovan and the NBomber
> contributors. Autobahn is an independent project, is not affiliated with or endorsed by
> NBomber or PragmaticFlow, and is developed separately from here on.

## Status

Early, but the foundation is in place. The engine has been **rewritten from F# into C# and
targets .NET 10**: one public API surface under `Autobahn.*`, no `FSharp.Core` anywhere in
the dependency graph, and clustering removed rather than left dormant. The suite that came
with the fork point is ported and green.

What is not built yet: metrics, thresholds, the protocol helpers, the CLI, and the live web
UI. Those are specified in [TODO.md](TODO.md), which is the plan of record. Expect the API
to keep moving while they land.

## Why a fork

NBomber 4.1.2 is a small, sharp, well-factored load-testing engine, and it is the last
version of it that is free software. Autobahn keeps that engine open under Apache-2.0 and
takes it in its own direction:

- **Open, permanently.** Apache-2.0, no paid tiers, no feature gates, no license server.
- **Pure C#, current .NET.** One language across the engine, the API, the tests and the
  UI, on .NET 10. The original engine is F#; every line of it was ported. That is a large,
  deliberate cost, paid once, so that the people most likely to contribute to a .NET
  load-testing tool can read and change every part of it — and so the engine can use what
  modern .NET actually offers.
- **Focused on the single-node engine.** Distributed/cluster execution is out of scope,
  and the cluster code inherited from the fork point is gone rather than left to rot.
- **A real UI.** A first-class live web interface served by the CLI, not just a console
  table and a static HTML file at the end.
- **Batteries in the box.** Metrics, thresholds and the common reporting integrations are
  part of the project rather than separate closed packages.

## Hello world

```csharp
using Autobahn;

var scenario = Scenario.Create("hello_world_scenario", async context =>
{
    // Put any logic here: an HTTP call, a SQL query, a gRPC request.
    // Autobahn measures how long it takes and whether it succeeded.
    await Task.Delay(100);

    return Response.Ok(statusCode: "200", sizeBytes: 1_024);
})
.WithLoadSimulations(
    Simulation.Inject(rate: 10,
                      interval: TimeSpan.FromSeconds(1),
                      during: TimeSpan.FromSeconds(30))
);

AutobahnRunner
    .RegisterScenarios(scenario)
    .Run();
```

A runnable version lives in [`examples/HelloWorld`](examples/HelloWorld):

```bash
dotnet run --project examples/HelloWorld
```

## Core concepts

| Concept | What it is |
|--|--|
| **Scenario** | One user journey. Runs in a loop, in parallel, for as long as the load model says. |
| **Step** | A named, measured slice inside a scenario, so one scenario can report several latencies. |
| **Load simulation** | The shape of the load over time: keep N copies constant, ramp them, inject at a fixed or random rate, or pause. Several compose into a plan. |
| **Response** | What a scenario or step returns: ok/fail, an optional payload, a status code, a size in bytes. |
| **Worker plugin** | Background work that runs alongside the test and contributes its own stats (e.g. ping). |
| **Metric** | A named numeric series over the run — counter, gauge or histogram — for anything latency and throughput do not describe. |
| **Report** | The end-of-run artifact: txt, csv, md, html. |

## Load simulations

```csharp
.WithLoadSimulations(
    Simulation.RampingConstant(copies: 50, during: TimeSpan.FromSeconds(30)),
    Simulation.KeepConstant(copies: 50, during: TimeSpan.FromMinutes(5)),
    Simulation.RampingInject(rate: 100, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30)),
    Simulation.Inject(rate: 100, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromMinutes(5)),
    Simulation.InjectRandom(minRate: 50, maxRate: 100, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromMinutes(1)),
    Simulation.IterationsForConstant(copies: 4, iterations: 200),
    Simulation.IterationsForInject(rate: 20, interval: TimeSpan.FromSeconds(1), iterations: 200),
    Simulation.Pause(during: TimeSpan.FromSeconds(10))
)
```

Closed-model simulations (`RampingConstant`, `KeepConstant`) control **concurrency**: how
many copies of the scenario are alive. Open-model simulations (`RampingInject`, `Inject`,
`InjectRandom`) control **arrival rate**: how many iterations start per interval,
regardless of how many are still running. Reach for the open model when you are testing a
system's capacity, and the closed model when you are simulating a fixed population of
users.

The two `IterationsFor…` simulations are counted rather than timed: they run an exact
number of iterations and then finish, whenever that happens to be. That is what makes a
load test usable as a smoke test, and what makes a small run reproducible.

## Shaping the mix

When several scenarios model one user population, give each a **weight** — its share of
the combined load — instead of hand-computing rates per scenario. Weights are all-or-
nothing: either every scenario in the run declares one, or none does.

```csharp
var browse   = Scenario.Create("browse", …).WithWeight(80);
var checkout = Scenario.Create("checkout", …).WithWeight(20);
```

Inside an iteration, the **copy's own index** and the **total copy count** are on
`context.ScenarioInfo`, and three helpers build on them so copies do not fight over the
same rows:

```csharp
context.OwnsIndex(i)                  // is row i this copy's?
context.Partition(rows)               // this copy's whole slice: copy 3 of 20 gets 3, 23, 43…
context.ItemForIteration(rows)        // one row per iteration, walking only this copy's slice
```

`Distribution` picks *which* work an iteration does, when the access pattern matters more
than the partitioning:

```csharp
Distribution.Uniform(keys)                       // every key equally likely
Distribution.Zipfian(keys, skew: 1.1)            // a hot minority - caches, content, feeds
Distribution.Multinomial(("read", 90), ("write", 10))
```

## Timeouts, hooks and stopping

```csharp
Scenario.Create("checkout", …)
    .WithIterationTimeout(TimeSpan.FromSeconds(2))   // recorded as "-102", not as a generic error
    .WithCompletionTimeout(TimeSpan.FromSeconds(30)) // grace for in-flight iterations at plan end
    .WithRestartIterationOnFail(false)               // a failed step no longer abandons the iteration
    .WithCompletionHook(ctx => Publish(ctx.Stats));  // fires with this scenario's final stats

await Step.Run("pay", context, () => PayAsync(), timeout: TimeSpan.FromSeconds(1));
```

A timed-out attempt is a distinct failure kind, so a report separates *slow* from *broken*.
Iterations still running when a scenario's plan ends get its completion timeout to finish
and be counted; the ones abandoned after that are logged with a count, because a hole in
the numbers is something an operator should be told about rather than left to infer.

Ending a run early never throws the results away — the scenarios wind down, the statistics
are calculated and the reports are written:

```csharp
AutobahnRunner.RegisterScenarios(scenario)
    .WithCancellationToken(token)   // cancelling ends the run early, reports and all
    .Run(args);
```

**Ctrl+C does the same thing** with no wiring at all. Press it once to stop the run and
keep what it measured; press it again to let the runtime kill the process.
`WithoutCancelKeyPress()` opts out and leaves Ctrl+C to the runtime. From inside a
scenario, `context.StopCurrentTest(reason)` and `context.StopScenario(name, reason)` are
the same early stop.

## Metrics

Latency, throughput, status codes and data transfer describe the *target*. A **metric** is
anything else worth a number: the queue you are draining, the cache you are missing, and
the load generator's own health.

Three kinds, registered by name off `context.Metrics` (asking twice hands back the same
metric, so a scenario can take it in `Init` and use it on the hot path):

```csharp
context.Metrics.Counter("cache.miss").Increment();              // a running total
context.Metrics.Gauge("queue.depth", MetricUnit.Count).Set(n);  // current value, last write wins
context.Metrics.Histogram("payload", MetricUnit.Kilobytes).Record(bytes);   // a distribution
```

A write is a single interlocked operation and allocates nothing, so one per iteration costs
about 24 ns — see `performance/Autobahn.Benchmarks/README.md`. `MetricUnit` says how a raw
value is displayed: record bytes, report kilobytes; the scale is applied once, when the
interval closes.

Everything lands on `SessionStats.Metrics`, ordered by name so a diff between two runs is a
diff of values rather than of row order, and on each `TimeLineHistoryRecord` for the run's
interval-by-interval view:

```csharp
var ratio = stats.Metrics.Single(x => x.Name == "cache.hit").Current;
```

**The load generator measures itself too.** CPU, working set, GC heap and collections,
thread-pool queue length and thread count, process threads, and socket bytes are collected
on their own timer without anyone asking, and shown live beside the scenario table:

```
runtime.cpu  runtime.working_set  runtime.gc_heap  runtime.gc_gen0/1/2
runtime.threadpool_queue  runtime.threadpool_threads  runtime.threads
runtime.socket_sent  runtime.socket_received
```

A load test that cannot show it was not itself the bottleneck is not evidence — that is why
these are on by default. `WithoutRuntimeMetrics()` turns them off. A counter that a platform
does not have is dropped for the rest of the run rather than failing it.

## Configuration

Anything set in code can be overridden by a JSON config, so the same test binary can be
gated differently per environment:

```jsonc
{
  "TestSuite": "checkout",
  "TestName": "peak hour",
  "TargetScenarios": [ "add_to_basket" ],

  "GlobalSettings": {
    "ScenariosSettings": [
      {
        "ScenarioName": "add_to_basket",
        "WarmUpDuration": "00:00:05",
        "LoadSimulationsSettings": [
          { "RampingInject": [50, "00:00:01", "00:00:30"] },
          { "Inject": [50, "00:00:01", "00:05:00"] }
        ],
        "CustomSettings": { "TargetHost": "https://staging.example.com" }
      }
    ],
    "ReportFolder": "./reports",
    "ReportFormats": [ "Html", "Csv" ],
    "ReportingInterval": "00:00:05"
  }
}
```

```csharp
AutobahnRunner
    .RegisterScenarios(scenario)
    .LoadConfig("./autobahn-config.json")
    .Run(args);          // --config, --infra and --target also work from the command line
```

`CustomSettings` is handed to the scenario's `Init` as an `IConfiguration`, so a scenario
binds it to whatever shape it likes.

## Logging

Logging is [Microsoft.Extensions.Logging](https://learn.microsoft.com/dotnet/core/extensions/logging)
with [ZLogger](https://github.com/Cysharp/ZLogger) behind it: `context.Logger` inside a
scenario is a plain `ILogger`, the run writes a rolling file next to its reports, and you
can take over completely:

```csharp
AutobahnRunner
    .RegisterScenarios(scenario)
    .WithMinimumLogLevel(LogLevel.Debug)
    .WithLogging(builder => builder.AddOpenTelemetry(/* ... */))
    .Run();
```

## Building

You need the .NET 10 SDK. From the repository root:

```bash
dotnet build
dotnet test
```

That is the whole story — no build script, no arguments, no bootstrapper. The tests run
real load tests in process, so the full suite takes a few minutes. To skip the slowest of
them:

```bash
dotnet test -- --treenode-filter "/*/*/*/*[Category!=slow]"
```

The examples and the web UI have their own solutions and are not part of the root build:

```bash
dotnet build examples/Examples.slnx
```

## Repository layout

```
Autobahn.slnx              the root solution: the engine, the CLI, the tests
src/Autobahn/              the engine and the public API
src/Autobahn.Cli/          the `autobahn` dotnet tool (skeleton)
src/Autobahn.Ui/           the Tesserae web UI (not started; own solution)
src/Autobahn.Ui.Contracts/ wire DTOs shared by the host and the UI
tests/Autobahn.Tests/      the test suite
examples/                  runnable examples (own solution)
performance/               BenchmarkDotNet guards for the hot paths (own solution)
assets/                    images
```

[CLAUDE.md](CLAUDE.md) has the architecture walkthrough and the conventions that matter
when changing the engine.

## Roadmap

[TODO.md](TODO.md) — features, fixes and improvements to bring in, plus the design of the
Tesserae-based web UI that the CLI will host.

## License

Apache License 2.0 — see [LICENSE](LICENSE). The fork point (NBomber 4.1.2) was released
under the same license; later NBomber versions are not, and no code from them is used
here.

## Acknowledgements

[NBomber](https://github.com/PragmaticFlow/NBomber) by Anton Moldovan and its contributors.
This project would not exist without it.
