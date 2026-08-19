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

Keep it that way. `Autobahn.slnx` is the only solution at the root and holds the engine,
the CLI and the test project — all of which build from a clean clone. If you add a project
to the root solution, adding one that cannot build from a clean clone breaks the plain
command for everyone.

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
- `src/Autobahn.Ui/Autobahn.Ui.slnx` — the web UI and its contracts. Building it needs the
  Transpose compiler installed as a global tool, which a clean clone does not have.

**CI is off.** There are no workflows at all: the inherited ones built a solution that no
longer exists and published under the upstream package identity, so they were deleted
rather than parked. Nothing runs on push, and the local commands above are the only check
there is. Writing the replacement is a roadmap item (see TODO.md).

## Architecture

The dependency direction is strictly one way:
**public API → Internal.Services → Internal.Domain → Internal.Infra**.

```
src/Autobahn/
  Scenario.cs, Step.cs, Response.cs, Simulation.cs,
  AutobahnRunner.cs, AutobahnContext.cs,
  ClientPool.cs, Time.cs, Converter.cs      the public API, one surface
  Constants.cs                              every tunable default in one place
  Contracts/                                IScenarioContext, IResponse, LoadSimulation, ScenarioProps…
  Stats/                                    the records the reports and the API read
  Configuration/                            JSON config model (autobahn-config.json)
  Plugins/                                  IWorkerPlugin, Network/Ping + PsPing
  Internal/
    Result.cs, AppError.cs, *Error.cs       validation results and every user-facing message
    Json/                                   System.Text.Json converters for config and the report view model
    Domain/
      SimulationPlan.cs                     validates and expands the load plan
      RuntimeScenario.cs, ScenarioFactory.cs
      ScenarioExecutionContext.cs           what user code sees inside a scenario
      StepExecution.cs, ScenarioExecution.cs   the measured wrappers
      HintsAnalyzer.cs                      post-run advice
      Concurrency/                          ScenarioActor, ScenarioActorPool
      Scheduler/                            ConstantActorScheduler, OneTimeActorScheduler, ScenarioScheduler
      Stats/                                RawMeasurementStats, Statistics, ScenarioStatsActor
    Infra/                                  ConsoleRender, LoggerBuilder, GlobalDependency, HostInfoProvider
    Services/
      ContextResolver.cs                    merges code config + JSON config + CLI args
      SessionRunner.cs                      session entry point
      TestHost/                             TestHost, TestHostScenario, TestHostConsole, ReportingManager, WorkerPlugins
      Reports/                              Txt, Csv, Md, Html, Console, TextTable, MarkdownDocument
  Resources/HtmlReport/                     embedded html/css/js for the static report
```

### Execution model

A run is a **session**: init → optional warm-up → bombing → clean → report.
`TestHost.RunSession` drives it.

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
  constructor is private, so only the six nested records exist, but exhaustiveness over
  them is *not* proven by the compiler. Every switch ends in a throwing default arm, and
  `LoadSimulationExhaustivenessTests` walks every case through every function that
  switches on one. Adding a case means that test fails until it is handled everywhere.
- **Timing is measured in ticks and time buckets**, not `DateTime`. Don't reintroduce
  wall-clock arithmetic on the hot path.
- **The console live table and the reports read the same stats records.** Changing a stats
  record means touching `Statistics.cs`, every report writer, and the console renderer.
- **The HTML report's view model is the serialized `SessionResult`.** Renaming a stats
  property silently breaks `Resources/HtmlReport/index.html` and its `index.js`, which
  address those names as strings. `ReportingTests` checks the document is assembled, not
  that every field is bound — read the template when you rename.
- **Spectre needs a width when there is no terminal.** With output redirected it collapses
  every table to an ellipsis, which is exactly the CI-log case; `SessionRunner` sets a
  fixed width in that situation. Rendering plain lines instead is TODO.md section 5.

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

## The web UI (planned)

A Tesserae-based live web interface, served by the Autobahn CLI over Kestrel from embedded
resources, is specified in detail in [TODO.md](TODO.md). The projects exist as empty
skeletons (`src/Autobahn.Ui`, `src/Autobahn.Ui.Contracts`) with their own solution; the CLI
(`src/Autobahn.Cli`) is an entry point and an argument surface. The engine must stay
usable, and fully headless, without any of them.
