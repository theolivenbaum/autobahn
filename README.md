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
| **Threshold** | A pass/fail rule over the stats or the metrics, checked while the run happens. Its verdict is the process exit code. |
| **Metric** | A named numeric series over the run — counter, gauge or histogram — for anything latency and throughput do not describe. |
| **Feed** | Where an iteration gets its data: circular, constant, random, batched or streaming, over CSV, JSON or a list. |
| **Report** | The end-of-run artifact: json, txt, csv, md, html. |

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

## Thresholds

A test that only reports numbers needs a human to read them. **Thresholds** are pass/fail
rules, checked on every reporting interval and again at the end:

```csharp
using static Autobahn.Thresholds.ThresholdComparison;
using static Autobahn.Thresholds.ThresholdSubject;

AutobahnRunner
    .RegisterScenarios(scenario)
    .WithThresholds(
        Threshold.ErrorRateBelow(0.02),
        Threshold.LatencyBelow(Percent99, 250).ForStep("reserve"),
        Threshold.RpsAbove(30).StartingAfter(TimeSpan.FromSeconds(12)),
        Threshold.Status("500", StatusCodeCount, LessThan, 50),
        Threshold.Metric("payments.attempted", MetricCurrent, GreaterThan, 100).OnlyAtTheEnd(),
        Threshold.ErrorRate(LessThan, 0.5).AbortingAfter(3))
    .Run(args);
```

A rule always states what it **requires**, and it can be scoped to a scenario, one of its
steps, a status code, or a metric. A rule that names no scenario applies to every scenario
in the run, tallied separately — one scenario's error rate says nothing about another's.

| Modifier | What it does |
|--|--|
| `.ForScenario(name)` | Narrows the rule to one scenario. |
| `.ForStep(name)` | Reads one step's numbers instead of the scenario's totals. |
| `.StartingAfter(t)` | Starts checking this far into the run, so ramp-up noise does not trip a steady-state rule. |
| `.OnlyAtTheEnd()` | One check, against the whole run. Cumulative claims need it. |
| `.AbortingAfter(n)` | Ends the run after `n` consecutive violations. Without it the rule is advisory. |
| `.Named(text)` | What the reports call it. |

Advisory is the default: the rule is recorded, reported, and it fails the run at the end,
but the load keeps going. `.AbortingAfter(n)` is the difference between a report saying a
service was down and not hammering a service that is already down.

**The verdict is the exit code.** A failed threshold sets the process exit code to `2`, so a
CI job that runs the test binary fails on its own; the run result says so either way:

```csharp
if (!stats.AllThresholdsPassed) { /* stats.Thresholds has every rule and how it fared */ }
```

`WithoutThresholdExitCode()` opts out. A rule that cannot mean what it says — a scenario the
run does not have, a subject that does not apply to its scope, a rate compared against 12 —
fails the run before any load is generated, because a gate that silently never checks
anything is worse than no gate.

Thresholds are declarable in the JSON config too, so the same binary can be gated
differently per environment (see below).

## Data feeds

A **feed** is where an iteration gets the data it works on. Three orders over any source:

```csharp
var users = Feed.Circular("users", FeedSource.FromCsv("users.csv", r => r["email"]));
var host  = Feed.Constant("host", hosts);                       // one, chosen once
var skus  = Feed.Random("skus", catalogue, seed: 42);           // uniform, reproducible
var pages = Feed.Batch("pages", rows, batchSize: 50);           // a group per iteration
var big   = Feed.Streaming("rows", FeedSource.StreamCsv("10m-rows.csv"));
```

`Feed.Circular` is the default choice: every item is used before any is reused. Reading one
is a single interlocked increment, so every copy of a scenario can pull from the same feed
without a lock. `Feed.Streaming` takes a lock per item — the price of not loading the file —
and reopens its source through the factory when it restarts.

**What happens when a finite feed runs out is stated, not assumed:**

```csharp
Feed.Circular("users", users, FeedExhaustion.Fail)   // Restart (default), Fail, StopScenario
```

Repeating the data quietly turns "each user is distinct" into a different test, so a feed
that must not repeat says so and throws `FeedExhaustedException` instead.

Sources are `FeedSource.FromCsv`, `FromJson`, `StreamCsv`, `StreamJson`, or any list you
already have. CSV rows come back keyed by the header (case-insensitively) unless you hand
over a mapping.

## The `autobahn` command line

A load test is still an ordinary .NET program that references the package and calls the
runner. The tool is the other route: point it at something that *exposes* scenarios, and it
builds the run around them so every option lives on the command line.

```bash
dotnet tool install -g Autobahn.Cli

autobahn list ./bin/Release/net10.0/LoadTests.dll
autobahn run  ./bin/Release/net10.0/LoadTests.dll -t checkout -f Json,Md -o ./reports
autobahn run  ./checkout.csx --show-config --reporting-interval 00:00:10
```

**From an assembly**: a scenario source is a public static property, or a public static
parameterless method, returning `ScenarioProps` or a sequence of them. Marking them
`[ScenarioSource]` is optional but says which members you meant:

```csharp
public static class Scenarios
{
    [ScenarioSource]
    public static ScenarioProps Checkout => Scenario.Create("checkout", …);
}
```

**From a script**: one `.cs` or `.csx` file, no project, no build. Its last expression is
what gets run, and `Autobahn`, `Autobahn.Feeds`, `Autobahn.Metrics` and `Autobahn.Thresholds`
are already imported:

```csharp
// checkout.csx
return Scenario.Create("checkout", async ctx =>
    {
        await Task.Delay(20, ctx.CancellationToken);
        return Response.Ok(statusCode: "200");
    })
    .WithoutWarmUp()
    .WithLoadSimulations(Simulation.Inject(rate: 50, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromMinutes(1)));
```

Exit codes are the contract: `0` ran and every threshold passed, `1` the command line or the
run was wrong, `2` ran and a threshold failed. `AutobahnExitCode` has the same three for a
program setting them itself.

The terminal dashboard and the web UI are not wired up yet — see [TODO.md](TODO.md) section 8.

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

Thresholds live there too. A rule under a scenario's settings block takes that scenario's
name from the block it sits in; rules from the config *add* to the ones declared in code
rather than replacing them, because the code says what the test is always about and the
config says what this environment additionally demands:

```jsonc
{
  "GlobalSettings": {
    "Thresholds": [
      { "Scope": "Scenario", "Subject": "ErrorRate", "Comparison": "LessThan", "Value": 0.01,
        "StartsAfter": "00:00:30", "Name": "stays reliable" },
      { "Scope": "StatusCode", "StatusCode": "500", "Subject": "StatusCodeCount",
        "Comparison": "LessThan", "Value": 10, "AbortAfter": 3 }
    ],
    "ScenariosSettings": [
      {
        "ScenarioName": "add_to_basket",
        "Thresholds": [
          { "Scope": "Scenario", "Subject": "Percent99", "Comparison": "LessThan", "Value": 500 }
        ]
      }
    ]
  }
}
```

```csharp
AutobahnRunner
    .RegisterScenarios(scenario)
    .LoadConfig("./autobahn-config.json")
    .Run(args);          // --config, --infra and --target also work from the command line
```

`CustomSettings` is handed to the scenario's `Init` as an `IConfiguration`, and
`GetCustomSettings<T>()` binds it to a type of your own. There is a **global**
`CustomSettings` block too, which every scenario sees and which a scenario's own block
overrides key by key — so a shared base URL is written once:

```jsonc
{
  "GlobalSettings": {
    "CustomSettings": { "TargetHost": "https://staging.example.com", "Tenant": "acme" },
    "ScenariosSettings": [
      { "ScenarioName": "add_to_basket", "CustomSettings": { "TargetHost": "https://basket.staging" } }
    ]
  }
}
```

### Precedence

Weakest to strongest: **defaults → code → JSON config → `AUTOBAHN_` environment variables →
command line**. Environment variables cover the scalar settings a CI job wants to change per
run (`AUTOBAHN_REPORT_FOLDER`, `AUTOBAHN_TARGET_SCENARIOS`, `AUTOBAHN_REPORT_FORMATS`,
`AUTOBAHN_REPORTING_INTERVAL`, `AUTOBAHN_TEST_SUITE`, `AUTOBAHN_TEST_NAME`,
`AUTOBAHN_REPORT_NAME`, `AUTOBAHN_ENABLE_HINTS`); a load plan or a threshold belongs in the
config file, where it can be read.

"Why is the report folder that?" is answerable from the run itself — `--show-config`, or
`ShowEffectiveConfig()` in code, prints every effective setting and the layer it came from:

```
Effective configuration:
  TestSuite            checkout                              [JsonConfig]
  TargetScenarios      add_to_basket                         [CommandLine]
  ReportFolder         ./reports                             [Environment]
  ReportingInterval    00:00:05                              [Default]
```

## Reports

Five formats, all written to `./reports/{sessionId}/` unless you pin a folder:

| Format | What it is |
|--|--|
| `Json` | **The run artifact.** The whole result as one versioned, machine-readable document. |
| `Html` | A self-contained page: every asset inlined, the result embedded as its view model. |
| `Txt` | The console summary, as a file. |
| `Md` | The one that pastes into a pull request. |
| `Csv` | One row per step, plus `_metrics.csv` and `_thresholds.csv` beside it. |

The **run artifact** is the primary one — the UI replays it, run-to-run comparison consumes
it, and a CI job asserts against it. Everything else is a rendering of the same data:

```jsonc
{
  "SchemaVersion": 1,
  "Producer": "Autobahn 0.1.0",
  "CompletedAt": "2026-08-19T10:14:03.5+00:00",
  "Result": { "FinalStats": { … }, "TimeLineHistory": [ … ], "Hints": [ … ] },
  "Plans": [ { "ScenarioName": "checkout", "LoadSimulations": [ … ] } ]
}
```

`SchemaVersion` is bumped when a field is removed or its meaning changes; adding one does
not bump it, so a reader that ignores unknown fields keeps working.

Autobahn only deletes files it wrote itself, and only its own log files — a pinned
`WithReportFolder` accumulates reports under their timestamped names rather than being
emptied on every run.

**Without a terminal** — a CI log — there is no live table: interval progress goes out as
one plain line per scenario through the ordinary logger, so it is in the log file too. With
a terminal, the live table owns the screen while it is up and log lines raised in the
meantime are replayed underneath it rather than drawn through it.

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
