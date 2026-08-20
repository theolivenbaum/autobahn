# CLAUDE.md

Guidance for working in this repository.

## What this repository is

**Autobahn** is a load-testing library for .NET: a hard fork of
[NBomber](https://github.com/PragmaticFlow/NBomber) at version **4.1.2**, the last release
published under Apache-2.0.

**It is a pure C# library on .NET 10.** The fork point was F# targeting `netstandard2.0`,
with a C#-friendly API layered over an F#-idiomatic one. That is gone: the engine, the API
and the tests are C#, there is one public surface, and no consumer needs `FSharp.Core`.
Three directional decisions govern everything below:

1. **Pure C#.** No F# anywhere, and no F#-only constructs on the public surface.
2. **.NET 10.** Not `netstandard2.0`, not multi-targeting. The one exception is the web
   UI's two projects, which target `netstandard2.0` because that is what Tesserae and the
   Transpose compiler build against — a framework constraint, not a language one;
   Transpose supports `LangVersion latest`. See TODO.md.
3. **No clustering.** The cluster code inherited from the fork point is removed.

[TODO.md](TODO.md) is the roadmap and the plan of record. [README.md](README.md) is the
user-facing introduction.

### Fork policy — read this before copying anything

The fork point is 4.1.2. **NBomber 5.x and later are not Apache-2.0, and no code, text, or
generated artifact from those versions may be brought into this repository.** TODO.md
describes later NBomber capabilities at the level of *what they do*, deliberately without
implementation detail, so they can be designed and written here from scratch. Treat any
request to "port" a newer NBomber file as out of bounds and say so.

The `4.1.2` branch is preserved untouched as the fork point. `main` is the development
branch.

### Out of scope

**Clustering** — coordinators, agents, distributed test execution, cluster monitoring — is
not part of Autobahn. The seams the fork point left behind are gone: the agent-stats intake
on the stats actor, the per-scenario cluster count in the test host, the coordinator/agent
node types, `ScenarioPartition`, `TestInfo.ClusterId`, and the stats-merging paths that
existed only to combine results from several nodes.

Do not reintroduce the concept under another name ("nodes", "workers", "shards"). If
distributed execution ever comes back, it will be designed fresh.

**Real-time reporting sinks** are also out. The `IReportingSink` contract, its registration
API and its plumbing were removed: Autobahn produces reports and console output, and the
live view of a running test is the web UI's job (TODO.md section 8), not a sink's.

## Build and test

Use the .NET 10 SDK. From the repository root, plain commands with no arguments work and
are the intended way to build and test:

```bash
dotnet build
dotnet test
```

Keep it that way. `Autobahn.slnx` is the only solution at the root and holds the engine, the
six protocol/export packages (`Autobahn.Http`, `Autobahn.WebSockets`, `Autobahn.Grpc`,
`Autobahn.Mqtt`, `Autobahn.Amqp`, `Autobahn.OpenTelemetry`), the CLI and the test project —
all of which build from a clean clone. Adding a project that cannot build from a clean clone breaks the plain command for
everyone.

**"Builds from a clean clone" means a clone, not this working tree.** The stock ignore rules
match at any depth, so `[Rr]eports/` — meant for the folder a run writes its reports into —
also matched `src/Autobahn/Internal/Services/Reports/`, where the engine's own report writers
live. Eleven source files were never committed and nobody noticed, because every build ran
against a tree that had them on disk. The `!src/**/…` negations in `.gitignore` are what stop
that, and they only help for names somebody thought of.

So the check is not "does it build" but "can git see it". Before pushing anything that adds a
folder: `git status --ignored --porcelain | grep '^!!'` lists what is being hidden, and a
`git clone` into a temp folder followed by `dotnet build` is what actually proves the claim.

The tests run on **TUnit**, on Microsoft.Testing.Platform. `global.json` opts `dotnet test`
into that runner (`"test": { "runner": "Microsoft.Testing.Platform" }`); without it the
.NET 10 SDK tries VSTest and fails. Most tests really do run short load tests in process,
so the full suite takes several minutes. The slowest are tagged `[Category("slow")]`:

```bash
dotnet test -- --treenode-filter "/*/*/*/*[Category!=slow]"
```

Run the *full* suite before pushing anything that touches the scheduler, the stats actor or
the reporting pipeline — those are the areas where a green filtered run still hides a
regression.

Not in the root build:

- `examples/Examples.slnx` — the examples. Kept separate so a routine build is the product,
  not the samples. Build it explicitly: `dotnet build examples/Examples.slnx`.
- `src/Autobahn.Ui/Autobahn.Ui.slnx` — the web UI. Building it needs the Transpose compiler
  installed as a global tool, which a clean clone does not have; `scripts/build-ui.sh` does
  it and stages the result into the CLI. See "The web UI" below.
- `performance/Performance.slnx` — the BenchmarkDotNet project. Run it before and after any
  change to the scheduler or the stats actor; `performance/Autobahn.Benchmarks/README.md`
  has the baseline and the command.

**CI** is `.github/workflows/ci.yml`: build, tests, the examples, the web UI, `dotnet
format --verify-no-changes`, a vulnerable-package check and a pack. A pull request gets the
fast test subset and `main` gets the whole suite, because a slow gate is a gate people learn
to ignore. It never pushes a package.

**Publishing** is `.devops/build-nuget.yml`, an Azure DevOps pipeline on a push to `main`, and
it is the only thing that publishes. The version is CalVer computed in the pipeline -
`yy.M.<build id mod 65536>`, the scheme the other packages from this organisation use - so no
release version is committed anywhere; the `VersionPrefix` in `Directory.Build.props` is the
local-build default and the pipeline's `/p:Version` overrides it. The push goes through the
`nuget-curiosity-org` service connection, so no API key lives in this repository. The pipeline
builds and stages the web UI before the solution, because the CLI embeds it, and runs the whole
test suite rather than the fast subset: a slow gate gets ignored, but a package published
without the slow tests cannot be un-published.

## Architecture

The dependency direction is strictly one way:
**public API → Internal.Services → Internal.Domain → Internal.Infra**.

```
src/Autobahn/
  Scenario.cs, Step.cs, Response.cs, Simulation.cs,
  AutobahnRunner.cs, AutobahnContext.cs,
  ClientPool.cs, Time.cs, Converter.cs      the public API, one surface
  Constants.cs                              every tunable default in one place
  ScenarioContextExtensions.cs              OwnsIndex / Partition / ItemForIteration
  Distribution.cs                           Uniform / Zipfian / Multinomial workload pickers
  Contracts/                                IScenarioContext, IResponse, LoadSimulation, ScenarioProps…
  Metrics/                                  IMetric/ICounter/IGauge/IHistogram, MetricKind, MetricUnit
  Thresholds/                               Threshold, ThresholdScope/Subject/Comparison
  Stats/                                    the records the reports and the API read
  Configuration/                            JSON config model (autobahn-config.json)
  Feeds/                                    IFeed, Feed factories, FeedSource, FeedExhaustion
  Plugins/                                  IWorkerPlugin, Network/Ping + PsPing
  Internal/
    Result.cs, AppError.cs, *Error.cs       validation results and every user-facing message
    Json/                                   System.Text.Json converters for config and the report view model
    Domain/
      SimulationPlan.cs                     validates and expands the load plan
      IterationBudget.cs                    hands out the iterations of a counted simulation
      RuntimeScenario.cs, ScenarioFactory.cs
      ScenarioExecutionContext.cs           what user code sees inside a scenario
      StepExecution.cs, ScenarioExecution.cs   the measured wrappers
      HintsAnalyzer.cs                      post-run advice
      Concurrency/                          ScenarioActor, ScenarioActorPool
      Metrics/                              the metric implementations, the registry, RuntimeMetrics
      Thresholds/                           ThresholdChecker, ThresholdState, subject reader, validation
      Feeds/                                the feed implementations and their validation
      Scheduler/                            ConstantActorScheduler, OneTimeActorScheduler, ScenarioScheduler
      Stats/                                RawMeasurementStats, Statistics, ScenarioStatsActor
    Infra/                                  ConsoleRender, LoggerBuilder, GlobalDependency, HostInfoProvider
    Services/
      ContextResolver.cs                    merges defaults + code + JSON config + env vars + CLI args
      EnvironmentConfig.cs, ProvenanceLog.cs  the environment layer, and where each value came from
      SessionRunner.cs                      session entry point
      TestHost/                             TestHost, TestHostScenario, TestHostConsole, ReportingManager, WorkerPlugins
      Reports/                              Json (the run artifact), Txt, Csv, Md, Html, Console, TextTable, MarkdownDocument
  Resources/HtmlReport/                     embedded html/css/js for the static report
```

### Execution model

A run is a **session**: init → optional warm-up → bombing → clean → report.
`TestHost.RunSession` drives it. It can also be ended early — from a cancellation token the
caller passed, from Ctrl+C, or from `context.StopCurrentTest` inside a scenario. All three
land on the same ordinary stop, so an early finish still winds the scenarios down, still
calculates statistics and still writes the reports. `TestHost._externalStopReason` is
deliberately sticky: a stop asked for during init has to survive the phase transitions that
reset `_stopped`.

Each target scenario gets a **`ScenarioScheduler`**, which walks that scenario's list of
load simulations in order. On each simulation interval it computes how much load should be
live right now and delegates to one of two actor schedulers:

- **`ConstantActorScheduler`** — closed model (`KeepConstant`, `RampingConstant`). Keeps a
  pool of long-lived `ScenarioActor`s at a target count, adding and removing to match.
- **`OneTimeActorScheduler`** — open model (`Inject`, `RampingInject`, `InjectRandom`).
  Each interval it starts N actors for exactly one iteration, renting from the pool and
  growing it when there aren't enough free actors.

A **`ScenarioActor`** loops: prepare the iteration, run the user's scenario function,
report the measurement, repeat. `IScenarioContext` is what the user's function receives —
logger, invocation number, scenario info, test info, and per-iteration data.

### Measurement and stats

Measurements are pushed into a per-scenario **`ScenarioStatsActor`**, a
`System.Threading.Channels` mailbox that owns all mutable stats state so nothing on the hot
path takes a lock. The mailbox message is a struct, so publishing a measurement allocates
nothing. The actor keeps two accumulators: an *interval* set (reset every reporting
interval, used for the live console table) and a *global* set (used for the final report).
Latency and data-size distributions are `HdrHistogram` recordings; measurements are
bucketed by time so a slow response is attributed to the interval it started in.

The **`ReportingManager`** ticks on a timer at the reporting interval and asks each
scheduler to close its interval, which is what feeds the console table and the timeline
behind the final report. At the end it builds `SessionStats`.

### Metrics

A second accumulator beside the stats pipeline, and deliberately not folded into it. The two
answer different questions and have different write patterns: a measurement is published once
per iteration through the stats actor's mailbox, while a metric is written whenever anything
feels like it. Putting metrics through the same channel would make every metric write queue
behind the measurements. They meet again only at the reporting interval.

`MetricsManager` (on `IGlobalDependency`) owns a `MetricRegistry` and, unless the user turned
it off, a `RuntimeMetrics` collector. A metric write is one interlocked operation on a field
and allocates nothing; each metric keeps an *interval* accumulator and a *global* one, the
same split the stats actor uses. `ReportingManager` closes the interval on its own tick, so
nothing else may call `CloseInterval` — the live console table reads `Registry.Global()`
instead, or it would take the numbers out of the timeline.

`RuntimeMetrics` samples twice a second on its own timer, independent of the reporting
interval. Every counter is read behind its own try/catch and dropped for the rest of the run
if the platform does not have it. Socket bytes come from an `EventListener` on the runtime's
`System.Net.Sockets` event source, which is entirely best-effort.

The metrics are reset when bombing starts, so the series they report cover the same window
every other number in the report does — warm-up is not part of it.

### Thresholds

`ThresholdChecker` holds one `ThresholdState` per rule *per scenario* — a rule that names no
scenario is a rule about each of them, tallied separately, because one scenario's error rate
says nothing about another's. `ReportingManager` checks them in the continuation of each
interval tick (the stats have to exist first) and once more at the end with `isFinal: true`,
against the whole run rather than the last interval. That final check is why a run shorter
than one reporting interval is still gated.

`ThresholdSubjectReader` is the only place that knows what each `ThresholdSubject` means. It
returns **null** rather than zero when a subject does not apply or the thing it names was
never produced, so a mismatched rule is a skipped check instead of one that silently passes
against a number nobody measured. `ThresholdValidation` runs before any load and rejects a
rule that cannot mean what it says.

The verdict is a process exit code, set in `SessionRunner.ApplyThresholdVerdict`: a library
cannot decide when a process exits, and throwing would take the reports with it. **Any test
that lets a threshold fail must opt out with `WithoutThresholdExitCode()` or reset
`Environment.ExitCode`** — it is process-wide, and a leaked failure fails the whole test run.

### The clock

`AutobahnContext.TimeProvider` is the clock the engine schedules on, `TimeProvider.System`
unless `WithTimeProvider` replaced it. It reaches the engine as `IGlobalDependency.Time` and
`ScenarioContextArgs.Time`, and it drives the reporting tick, the warm-up cut-off, the gap
between simulation intervals, the actor start jitter, the step and iteration timeouts, the
shutdown poll and the runtime-metrics sampler.

What it does not drive is measurement - see "Two clocks" below - so a run on a fake clock
finishes in a fraction of its planned wall clock while still reporting the latencies its
scenario actually took. `TimeProviderTests` runs a thirty-second plan, all 300 iterations of
it, in under four seconds.

### The load generator's own scheduling

Autobahn sets server GC and concurrent GC in the shipped projects, and
`GCLatencyMode.SustainedLowLatency` for the duration of a run. It sets **nothing** on the
thread pool, deliberately: forcing a large minimum hides a scenario that blocks rather than
fixing it, and produces the same starvation later with no queue left to diagnose it from.

The assumption is that a scenario is genuinely asynchronous. `HintsAnalyzer.AnalyzeLoadGenerator`
is what catches it when one is not — a thread-pool queue that never empties, sustained CPU
above 85%, or a run full of gen2 collections all mean the numbers describe the generator. The
thresholds there are deliberately loud rather than precise: a hint that fires on a healthy run
gets ignored, and then so does the one that mattered.

### Logging

Logging is `Microsoft.Extensions.Logging` with **ZLogger** providers behind it. There are
two loggers on purpose: `dep.ConsoleLogger` (what the operator watches) and `dep.Logger`
(the rolling file, and anything the user attached with `AutobahnRunner.WithLogging`). The
`dep.LogInfo` / `LogWarn` / `LogError` / `LogFatal` helpers write to both; anything logged
straight through `dep.Logger` stays out of the console. `context.Logger` inside a scenario
is the file/user logger.

### Things that will bite you

- **`Autobahn.Internal.Domain.Stats` shadows `Autobahn.Stats`.** Inside the domain
  namespaces, an unqualified `Stats.Foo` resolves to the internal one. Use a `using
  Autobahn.Stats;` and unqualified type names rather than a `Stats.` prefix.
- **`LoadSimulation` is a closed hierarchy, not a union the compiler checks.** The base
  constructor is private, so only the eight nested records exist, but exhaustiveness over
  them is *not* proven by the compiler. Every switch ends in a throwing default arm, and
  `LoadSimulationExhaustivenessTests` walks every case through every function that
  switches on one. Adding a case means that test fails until it is handled everywhere.
- **A counted simulation gets no start jitter.** Copies are normally spread across the
  simulation interval so they do not all fire at once, but a counted segment's work is a
  fixed number of iterations rather than a duration, so a copy that waits out the jitter
  can find the whole budget already handed out and never run. `ScenarioScheduler.RunSegment`
  zeroes the jitter for those segments; changing that silently makes `copies:` a lie.
- **Timing is measured in ticks and time buckets**, not `DateTime`. Don't reintroduce
  wall-clock arithmetic on the hot path.
- **Two clocks, and the split is deliberate.** Everything the engine *waits* on goes through
  `dep.Time` / `ScenarioContextArgs.Time` - a `TimeProvider` the caller can replace with
  `WithTimeProvider`. Everything it *measures* with is `Stopwatch`. `Stopwatch.GetTimestamp`
  is a static intrinsic and `TimeProvider.GetTimestamp` is a virtual call, so moving the
  measurement path onto the provider would cost one virtual call per measurement to make a
  number nothing ever fakes fakeable. Keeping them apart is also what makes a faked run
  honest: a fake clock changes when a run does things, never what it reports having observed.
  A new `Task.Delay` or `new Timer` in the engine belongs on the provider; a new latency
  reading belongs on the `Stopwatch`.
- **`MetricUnit` carries its own decimal precision, and the default is not zero.**
  `MetricUnit.None` keeps two decimals because a bare number could be anything;
  `Count` and `Bytes` keep none. A unit with the wrong precision silently rounds a
  fractional histogram to zero.
- **The console live table and the reports read the same stats records.** Changing a stats
  record means touching `Statistics.cs`, every report writer, and the console renderer.
- **The HTML report's view model is the serialized `SessionResult`.** Renaming a stats
  property silently breaks `Resources/HtmlReport/index.html` and its `index.js`, which
  address those names as strings. `ReportingTests` checks the document is assembled, not
  that every field is bound — read the template when you rename.
- **The live table owns the terminal while it is up.** Spectre's live display redraws in
  place and cannot let another writer put a line above it, so `ConsoleRender` holds console
  writes between `BeginLiveDisplay` and `EndLiveDisplay` and replays them after. Anything
  that writes to the console must go through `ConsoleRender.Render` or
  `ConsoleRender.WriteOrDefer` — an `AnsiConsole.Write` straight from somewhere else will
  land in the middle of the table. The file log is never deferred.
- **The reporting timer starts with the run.** It used to wait three seconds, which put every
  interval three seconds out of step with its own label. Don't reintroduce a start delay;
  `ReportingManagerDrainDelay` is a stop-side drain for in-flight mailbox messages and must
  stay far shorter than a reporting interval.
- **The run artifact is versioned, the renderings are not.** `RunArtifact` is what the UI and
  run-to-run comparison read, so removing a field or changing its meaning means bumping
  `Constants.RunArtifactSchemaVersion`. It also serializes the session result *before*
  `Report.AppendGlobalInfoStep` folds the scenario's own numbers in as a pseudo-step — the
  artifact records the run as measured, not as the reports render it.
- **Config precedence lives in one place and records itself.** `ContextResolver` resolves
  defaults → code → JSON config → `AUTOBAHN_` env vars → CLI, and writes the winner into a
  `ProvenanceLog` as it goes. Don't reconstruct provenance afterwards — that means writing
  the precedence rules twice and having the two drift.
- **A feed is read by every scenario copy at once.** The in-memory ones are lock-free by
  construction (one interlocked increment over an array that is never written after
  construction); anything added here has to keep that, or say plainly why it cannot, as
  `StreamingFeed` does.
- **Spectre needs a width when there is no terminal.** With output redirected it collapses
  every table to an ellipsis, which is exactly the CI-log case; `SessionRunner` sets a
  fixed width in that situation, and skips the live table entirely there.

## The protocol helpers

Six packages beside the engine, each with its own `.csproj` in the root solution so a plain
`dotnet build` covers them: `Autobahn.Http` (with HAR conversion), `Autobahn.WebSockets`,
`Autobahn.Grpc`, `Autobahn.Mqtt`, `Autobahn.Amqp` and `Autobahn.OpenTelemetry`. They depend
on the engine and never the other way round; the engine must stay usable with none of them
installed. The CLI references `Autobahn.Http` because `autobahn record` generates HTTP
scenario source.

- **The HTTP factories live on `HttpRequest`, not on a class called `Http`.** A class with the
  same name as its own namespace binds to the *namespace* inside anything under a shared root,
  so `Http.Get` fails to compile in half the places it would be written — including this
  repository's own tests. Don't reintroduce the facade.
- **`HttpRequest` is a description, not an `HttpRequestMessage`.** One of those can only be
  sent once; a scenario that holds a request across iterations must keep working.
- `HttpSize` counts the HTTP/1.1 wire form: request line, status line, headers, both bodies.
  It is an approximation and says so — it is before TLS and before HTTP/2 header compression,
  because those happen below where any of it is visible.
- **OTLP is not a sink coming back.** `AutobahnContext.OnInterval` is one delegate the engine
  calls with a record it already built; it has no lifecycle and user code implements nothing.
  It is invoked without being awaited, and a failure is logged rather than propagated — an
  export that broke is not a reason to lose the test.
- `AutobahnMeter` uses observable gauges rather than counters: the stats already exist as a
  per-interval snapshot, and re-deriving deltas so a counter could be incremented would be
  arithmetic in service of the wrong shape. Every tag is the *identity* of the thing measured;
  a tag whose value changes each interval would make every interval its own time series.
### The message brokers

`Autobahn.Mqtt` and `Autobahn.Amqp` offer the same two shapes the WebSocket helper does,
because those are properties of messaging rather than of a transport: request/response with
the caller supplying the correlation, and publish-then-consume where a publisher scenario and
a consumer scenario are independent and the number being measured is the delivery latency
*between* them.

- **`PublishStamped` / `ReceiveStamped` are the pair that measures delivery.** The publisher
  writes `Stopwatch.GetTimestamp()` into the message and the consumer reads it back, because
  two independent scenarios have nowhere else to put it. Monotonic and process-local on
  purpose: both scenarios are in one load generator, and a wall clock can step backwards
  mid-run and report a negative latency. A message with no stamp is a **failure**, not a zero -
  reporting a foreign publisher's message as instant delivery would be the most flattering
  possible lie.
- **MQTT stamps the payload; AMQP stamps a header.** MQTT 3.1.1 has no user properties, and a
  helper that only worked against MQTT 5 brokers would work against half of them. AMQP has
  headers, so the body stays exactly what the test wrote.
- **A plain `Receive` reports how long the iteration waited**, which is a property of the test
  rather than of the broker: a consumer with nothing to consume waits as long as the publisher
  takes. That is why the stamped pair exists and why it is a separate method.
- **The inbox is bounded and drops the oldest**, with a `Dropped` count. An unbounded inbox in
  a load test is a memory leak waiting for a slow consumer; dropping is a finding, and every
  latency reported after a drop is optimistic.
- **MQTT pools connections, AMQP pools channels over one connection.** Not an inconsistency:
  an MQTT connection *is* the session, so N users on one is a different test, while an AMQP
  connection is a transport that multiplexes sessions and a channel is the per-user thing. The
  AMQP pool owns the shared connection (`AmqpPool`), because the first copy to finish must not
  close the transport the rest are still using.
- **Setup calls come in two forms.** `SubscribeAsync`/`DeclareQueueAsync`/`ConsumeAsync` take a
  `CancellationToken` and throw; the `Subscribe`/`DeclareQueue`/`Consume` overloads take an
  `IScenarioContext` and return a `Response`. `WithInit` has no scenario context, and a
  subscription that could not be made is a test that cannot run rather than a slow iteration.
- **MQTT is tested against a broker in this process** (`MQTTnet.Server`, a test-only package -
  a client package must never ship a broker). **AMQP has no such thing**, so its integration
  tests skip when nothing is listening and say how to get one: `AUTOBAHN_AMQP_URI`, or
  `docker run --rm -p 5672:5672 rabbitmq:4-alpine`. Everything testable without a broker - the
  stamp, the guards, the connect-failure path - runs everywhere.

`tests/Autobahn.Tests/TestServer.cs` is a real `HttpListener` on a real port, because these
  tests are about the wire. A test that creates an `AutobahnMeter` must filter its
  `MeterListener` by the meter's **version** — every instance shares the name, so a
  name-only filter also picks up whatever a sibling test is publishing.

## The CLI

`src/Autobahn.Cli` is a real front end now, not a skeleton: `autobahn run` and `autobahn list`
point at a built assembly or a single C# script and build the run around the scenarios it
exposes. `Autobahn.Cli.csproj` still packs as the `autobahn` dotnet tool.

- **Its assembly is `Autobahn.Cli`, not `autobahn`.** Assembly identities are compared
  case-insensitively, so an assembly named `autobahn` shadows the engine's `Autobahn` and
  every engine type fails to load at runtime. The command is `autobahn` because of
  `ToolCommandName`, which is unrelated. Don't "fix" the assembly name to match the command.
- Assembly discovery loads the target in its own `AssemblyLoadContext` with an
  `AssemblyDependencyResolver`, so the target's dependencies win over the tool's — except
  `Autobahn` itself, which is deliberately not redirected: the two have to agree on
  `ScenarioProps` or nothing found could be run.
- Scripts go through Roslyn (`Microsoft.CodeAnalysis.CSharp.Scripting`). The script's last
  expression is its result and must be a `ScenarioProps` or a sequence of them.
- Unlike `CommandLineArgs` (the in-process parser, where an unknown argument belongs to the
  test runner and must be ignored), `CliParser` treats an unknown option as an error. At a
  prompt a mistyped flag that silently does nothing is worse than one that stops.
- **`autobahn record` is not browser-driven load testing and must not become it.** It drives
  one Playwright session, records what the page requested, and emits scenario source that an
  *HTTP client* then runs under load. Browsers under load make the generator the bottleneck
  and measure the generator; the whole point is to learn from a browser and then not use one.
- The generator lives in `Autobahn.Http` (`ScenarioCodeGenerator`), not the CLI, so it is
  testable without a browser and works from a HAR too. Its output has to *compile*: the tests
  load a generated script back through `ScriptScenarioLoader`, because checking the shape of
  the text would not catch an unescaped quote. `ScriptScenarioLoader` touches
  `Autobahn.Http.HttpRequest` before enumerating loaded assemblies for the same reason —
  assemblies load lazily, and a generated script would otherwise fail to compile against a
  package the tool had not happened to load yet.

## Conventions

C# style:

- `internal` by default; public is a deliberate decision about the supported surface.
- Records and `readonly struct` for data that does not mutate; classes where identity or
  mutation is the point (the actors, the schedulers, the stats state).
- `required` + `init` on records rather than positional parameters, except where the type
  really is a tuple of values (the `LoadSimulation` cases, `Measurement`).
- `async`/`await`, with `ConfigureAwait(false)` on library paths.
- Nullable reference types enabled, and honestly annotated — not blanket `!`.
- Validation returns `Result<T>` rather than throwing; the error carries its own message.
- File-scoped namespaces, one type per file, folder structure mirroring the namespace.

Rules:

- Keep tunables in `Constants` rather than inlining magic numbers.
- New user-facing errors are a record under `Internal/*Error.cs`, so the message formatting
  stays in one place; include the scenario name in anything scenario-scoped.
- Public API additions need an example under `examples/` and coverage under `tests/`.
- Prefer strong typing. `object`/boxing on the measurement path is a performance decision,
  not a style one — don't add more of it.
- Prefer writing thirty lines over taking a dependency for formatting. `TextTable` and
  `MarkdownDocument` exist because the packages they replaced brought an `FSharp.Core`
  reference and a NullReferenceException respectively.

## Testing

`tests/Autobahn.Tests` is **TUnit**. Assertions are `await Assert.That(x).IsEqualTo(y)`;
data-driven cases use `[Arguments(...)]` and `[MethodDataSource(...)]`.

- Tests that start a real load test are marked `[NotInParallel]`. TUnit runs tests
  concurrently by default, and two load tests sharing a machine measure each other.
- Test classes that touch `internal` types must themselves be `internal` — a public method
  cannot take an internal parameter. TUnit discovers internal classes fine.
- Assert on invariants (ordering, ratios, "at least N") rather than exact counts, except
  where an open-model plan makes the count genuinely deterministic (`Inject` at rate R for
  N intervals is R×N).
- Anything needing more than a few seconds of wall clock gets `[Category("slow")]`.
- There is no property-testing library. The fork point's FsCheck properties are expressed
  as explicit `[Arguments]` cases plus seeded random sweeps — same invariants, no
  `FSharp.Core` in the test project.

## The web UI

A live web interface for a running test: `autobahn run --ui` starts Kestrel beside the run
and prints a URL. The page is a Tesserae application written in C# and compiled to
JavaScript by Transpose, embedded in the CLI assembly. TODO.md section 8 is the
specification; the engine must stay usable, and fully headless, without any of it.

```
src/Autobahn.Ui.Contracts/     the wire DTOs, multi-targeted netstandard2.0 + net10.0
src/Autobahn.Ui/src/           the Tesserae application
  App.cs, Shell.cs             entry point, the rail and the run's header
  RunClient.cs                 snapshot, socket, backfill, control
  DashboardState.cs            everything the views bind to, as observables
  Widgets.cs, Format.cs        the shapes and the number formatting
  Views/                       one file per screen
src/Autobahn.Cli/Ui/           the host: UiServer, UiSession, RunFeed, FrameBuilder, PastRuns
```

**The run must not be able to tell whether anyone is watching.** Everything the UI reads
comes out of a `RunFeed` the run writes to once per reporting interval; a slow client drops
frames from a bounded queue rather than applying back-pressure. The engine seams the host
uses are `WithSessionStartObserver` (once, with the resolved run) and `WithIntervalObserver`
(the record the reporting manager already built) - not hooks the engine grew for a UI.

**The UI is the live view and only the live view.** It renders a running test. A finished run
is rendered by the report writers under `Internal/Services/Reports/` - the handwritten HTML
report for a person, the json run artifact for a machine - and those stay handwritten. There
was a static export that rendered this application against a finished run's artifact; it was
removed. Don't build it again: a record of a finished run is a document, and shipping twelve
megabytes of application to render a hundred and fifty kilobytes of table is the wrong shape.
The one thing the dashboard does read from finished runs is their artifacts, for the
run-to-run comparison screen - which is a live-run feature, because the run it compares
against is the one being watched.

### Building it

Not part of `dotnet build`: the Transpose compiler is a global tool a clean clone does not
have, and `UiAssets` serves an explanatory page when the assets are absent.

```bash
dotnet tool update --global Transpose.Compiler
export PATH="$PATH:$HOME/.dotnet/tools"
./scripts/build-ui.sh Release        # compiles the app and stages it into the CLI
dotnet build src/Autobahn.Cli/Autobahn.Cli.csproj
```

The staged output under `src/Autobahn.Cli/Ui/wwwroot/` is gitignored - it is built, not
authored. The script gzips every file, because an assembly stores an embedded resource
uncompressed and this is twelve megabytes of JavaScript, CSS and icon fonts.

### Things that will bite you in the UI

These are all consequences of one end being a browser, and every one of them fails
*silently* rather than loudly.

- **The DTOs are compiled into the UI assembly, not referenced.** `Autobahn.Ui.csproj` links
  `../Autobahn.Ui.Contracts/*.cs` rather than taking a project reference, because Transpose
  emits reflection metadata only for the assembly it is compiling - and without metadata,
  Newtonsoft deserializes every record to its own defaults and reports no error. Both ends
  still compile the same source, which is the point of the contracts project.
- **Every DTO declares its own parameterless constructor.** A record gets one anyway, but the
  compiler marks it synthetic and the deserializer skips synthetic members.
- **The client deserializes with `ObjectCreationHandling.Replace`.** The default reuses what a
  property already holds, so a collection initialized to `[]` stays empty for every document.
- **No `long` on the wire, and no `DateTimeOffset`.** The transpiled BCL models a 64-bit
  integer as an object, and JSON cannot say which numbers are those; counts and epoch
  milliseconds are `double`. Times are milliseconds since the epoch.
- **Enum values are serialized in their declared casing while property names are camelCase.**
  The client parses enums by name and that parse is case-sensitive.
- **`HttpClient` needs an absolute URI.** Transpose implements it over fetch, and a relative
  path throws rather than resolving against the page.
- **A delegate interpolated into `Script.Write` is re-bound at the call site.** That is why the
  WebSocket handlers use the typed `Transpose.Core.dom.WebSocket` binding rather than
  assigning `onopen` from a string.
- **`Series(params ChartSeries[])` replaces the chart's series rather than appending.** Two
  calls leave only the second one's data, and the axis collapses onto it.
- **The token is issued as a cookie by the first authorised request.** A browser does not
  carry a query string onto the sub-resources a page asks for, so the token in the printed
  URL authorises the document and nothing in it.
