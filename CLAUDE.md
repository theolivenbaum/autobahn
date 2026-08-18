# CLAUDE.md

Guidance for working in this repository.

## What this repository is

**Autobahn** is a load-testing framework for .NET: a hard fork of
[NBomber](https://github.com/PragmaticFlow/NBomber) at version **4.1.2**, the last release
published under Apache-2.0. The engine is F# targeting `netstandard2.0`, with a C#-first
public API surface layered on top.

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
not part of Autobahn. Do not add it, and do not carry cluster-shaped abstractions into new
code. Some cluster seams still exist in the 4.1.2 code (`AddFromAgent` on the stats actor,
`getScenarioClusterCount` in the test host, coordinator/agent node types); leave them alone
unless you are deliberately removing them, and never build on them.

## Build and test

```bash
dotnet restore NBomber.sln
dotnet build NBomber.sln
dotnet test tests/NBomber.IntegrationTests/NBomber.IntegrationTests.fsproj --filter CI!=disable
```

The `CI!=disable` filter skips tests that need long wall-clock time or external services.
Run the full suite locally before pushing anything that touches the scheduler, the stats
actor or the reporting pipeline — those are the areas where a green filtered run still
hides a regression.

`build.cake` is the legacy Cake pipeline inherited from NBomber. It still references
PragmaticFlow plugin repositories and a `src/NBomber.Contracts` project that does not exist
here; it is not part of the working build and will be replaced (see TODO.md).

## Architecture

The dependency direction is strictly one way: **Api → DomainServices → Domain → Extensions/Infra**.

```
src/NBomber/
  Api/               public surface: Shared.fs, FSharp.fs, CSharp.fs
  Contracts.fs       NBomberContext, ScenarioProps, the types users touch
  Configuration.fs   JSON config model (nbomber-config.json / infra-config.json)
  Constants.fs       every tunable default in one place
  Domain/            the engine
    LoadSimulation.fs        validates and expands the load plan
    Scenario.fs, Step.fs     runtime shapes of a scenario/step
    ScenarioContext.fs       what user code sees inside a scenario
    Concurrency/             ScenarioActor, ScenarioActorPool
    Scheduler/               ConstantActorScheduler, OneTimeActorScheduler, ScenarioScheduler
    Stats/                   RawMeasurementStats, Statistics, ScenarioStatsActor
    HintsAnalyzer.fs         post-run advice
  DomainServices/
    NBomberContext.fs        merges code config + JSON config + CLI args
    NBomberRunner.fs         session entry point
    TestHost/                TestHost, TestHostScenario, TestHostConsole,
                             ReportingManager, ReportingSinks, WorkerPlugins
    Reports/                 Txt, Csv, Md, Html, Console
  Infra/               Console.fs, Dependency.fs (Serilog, DI-ish globals)
  Extensions/          DataSet.fs (data feeds), Internal.fs
  Plugins/             PingPlugin, PsPingPlugin
  Resources/HtmlReport/  embedded html/css/js for the static report
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

Measurements are pushed into a per-scenario **`ScenarioStatsActor`**, a mailbox that owns
all mutable stats state so nothing on the hot path takes a lock. It keeps two accumulators:
an *interval* set (reset every reporting interval, used for real-time stats and sinks) and
a *global* set (used for the final report). Latency and data-size distributions are
`HdrHistogram` recordings; measurements are bucketed by time so a slow response is
attributed to the interval it started in.

The **`ReportingManager`** ticks on a timer at the reporting interval, asks each scheduler
for its interval stats, feeds the console live table and every registered `IReportingSink`,
then builds the final `NodeStats` at the end.

### Things that will bite you

- **`NBomber.fsproj` compile order is load-bearing.** F# resolves top to bottom. A new file
  must be added to the `<Compile Include>` list in the right position or the build fails in
  a confusing place. Adding a file and forgetting the fsproj entry silently excludes it.
- **`NBomber.Contracts` is an external NuGet package** pinned to `[4.1.1]`, not source in
  this repo, even though `src/NBomber/Contracts.fs` also exists (that file holds the
  runner-side context types; the package holds `IScenarioContext`, `Response`, the stats
  records). Vendoring it into the fork is a roadmap item and a prerequisite for the
  namespace rename.
- **The public API exists three times.** `Api/Shared.fs` (common), `Api/FSharp.fs` (F#
  idiomatic), `Api/CSharp.fs` (C#-friendly overloads, `[<Extension>]` methods,
  `ParamArray`). A new user-facing capability needs all the relevant ones or it is missing
  for half the users.
- **Timing is measured in ticks and time buckets**, not `DateTime`. Don't reintroduce
  wall-clock arithmetic on the hot path.
- **The console live table and the reports read the same stats records.** Changing a stats
  record means touching `Statistics.fs`, every report writer, and the console renderer.

## Conventions

- F# style follows the existing code: `module internal` for engine internals, records over
  classes, `inline` on hot-path helpers, `Result`/`taskResult` (FsToolkit) for anything
  that validates.
- Keep tunables in `Constants.fs` rather than inlining magic numbers.
- New user-facing errors go through `Domain/Errors.fs` so the message formatting stays in
  one place; include the scenario name in anything scenario-scoped.
- Public API additions need an example under `examples/` and coverage under
  `tests/NBomber.IntegrationTests/`.
- Prefer strong typing. `obj`/boxing on the measurement path is a performance decision, not
  a style one — don't add more of it.

## Testing

`tests/NBomber.IntegrationTests` is xUnit + FsCheck + Unquote, and most tests really do
run short load tests in-process. That makes them slow and slightly timing-sensitive:
assert on invariants (ordering, ratios, "at least N") rather than exact counts, and mark
anything that needs more than a few seconds or an external service with the `CI=disable`
trait.

`performance/` holds BenchmarkDotNet projects for the scheduler and stats hot paths. Use
them when changing anything under `Domain/Scheduler` or `Domain/Stats`.

## The web UI (planned)

A Tesserae-based live web interface, served by the Autobahn CLI over Kestrel from embedded
resources, is specified in detail in [TODO.md](TODO.md). It does not exist yet. When it
lands it will live in its own projects (`Autobahn.Cli`, `Autobahn.Ui`, `Autobahn.Ui.Contracts`)
and the engine must stay usable, and fully headless, without it.
