# CLAUDE.md

Guidance for working in this repository.

## What this repository is

**Autobahn** is a load-testing library for .NET: a hard fork of
[NBomber](https://github.com/PragmaticFlow/NBomber) at version **4.1.2**, the last release
published under Apache-2.0.

**The target is a pure C# library on .NET 10.** The code you will find in the tree today is
still the fork point: F#, targeting `netstandard2.0`, with a C#-friendly API layered over an
F#-idiomatic one. That is the starting state, not the destination. Three directional
decisions govern everything below:

1. **Pure C#.** Every F# file in the engine gets rewritten in C#. No new F# is added.
2. **.NET 10.** Not `netstandard2.0`, not multi-targeting. Use what the current runtime
   offers rather than working around a decade-old floor. The one planned exception is the
   web UI's projects, which have to meet the Transpose compiler at `netstandard2.0` — see
   TODO.md.
3. **No clustering.** The cluster code inherited from the fork point is removed, not
   preserved.

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
not part of Autobahn, and the seams the fork point left behind are being deleted rather
than kept dormant: `AddFromAgent` on the stats actor, `getScenarioClusterCount` in the test
host, the coordinator/agent node types, and the stats-merging paths that exist only to
combine results from several nodes.

Deleting them is not tidiness for its own sake. They force the stats pipeline to be
merge-shaped when it only ever merges one node's results, and porting that shape to C#
would carry the complexity forward for no user. When you touch a file that still has a
cluster seam, take the seam out. Never build on one, and do not reintroduce the concept
under another name ("nodes", "workers", "shards") — if distributed execution ever comes
back, it will be designed fresh.

## Build and test

Use the .NET 10 SDK. Until the port completes, the tree still builds as the F# solution it
was forked as:

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

The layout below is the fork point's. The port to C# keeps this shape — the layering is
sound and is the reason the engine is portable at all — but renames files to `.cs`, and the
`Api/FSharp.fs` / `Api/CSharp.fs` split collapses into one surface. Read it as the map of
what exists and what the C# version is expected to look like.

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

- **`NBomber.fsproj` compile order is load-bearing** *while the engine is still F#*. F#
  resolves top to bottom: a new file must go into the `<Compile Include>` list in the right
  position or the build fails somewhere confusing, and a file added without an fsproj entry
  is silently excluded. This trap disappears with the last F# file — one of the smaller
  reasons the port is worth doing.
- **`NBomber.Contracts` is an external NuGet package** pinned to `[4.1.1]`, not source in
  this repo, even though `src/NBomber/Contracts.fs` also exists (that file holds the
  runner-side context types; the package holds `IScenarioContext`, `Response`, the stats
  records). It is an F# assembly this fork does not control, so it blocks both the C# port
  and the rename. Vendoring it is the first roadmap item for a reason.
- **The public API exists three times.** `Api/Shared.fs` (common), `Api/FSharp.fs` (F#
  idiomatic), `Api/CSharp.fs` (C#-friendly overloads, `[<Extension>]` methods,
  `ParamArray`). Until the port collapses this into one C# surface, a user-facing change
  needs all the relevant ones or it is missing for half the users.
- **Timing is measured in ticks and time buckets**, not `DateTime`. Don't reintroduce
  wall-clock arithmetic on the hot path.
- **The console live table and the reports read the same stats records.** Changing a stats
  record means touching `Statistics.fs`, every report writer, and the console renderer.

## Conventions

**New code is C#.** Write C# even when the file next to it is F#, unless you are editing an
existing F# file in place. Do not add F# files. Do not add F#-only constructs to the public
surface: no F# functions, options, discriminated unions or records where a C# caller has to
reference `FSharp.Core` to use them.

C# style for the ported engine:

- `internal` by default; public is a deliberate decision about the supported surface.
- Records and `readonly struct` for data that does not mutate; classes where identity or
  mutation is the point (the actors, the schedulers, the stats state).
- `async`/`await` with `ValueTask` where a hot path usually completes synchronously.
- Nullable reference types enabled, and honestly annotated — not blanket `!`.
- Validation returns a result type rather than throwing. The F# code uses `Result`/
  `taskResult` from FsToolkit; the C# version needs one small result type of its own, not a
  dependency on an F# library.
- File-scoped namespaces, one type per file, folder structure mirroring the namespace.

Rules that survive the language change:

- Keep tunables in `Constants` rather than inlining magic numbers.
- New user-facing errors go through the errors module so the message formatting stays in
  one place; include the scenario name in anything scenario-scoped.
- Public API additions need an example under `examples/` and coverage under `tests/`.
- Prefer strong typing. `obj`/boxing on the measurement path is a performance decision, not
  a style one — don't add more of it.

### Porting an F# file

- Port a whole file at a time and keep its tests green across the change. A half-ported
  module that has to interop both ways is worse than either end state.
- Behaviour first, idiom second: get the C# passing the existing tests, then make it read
  like C#. Do not "improve" semantics during a port — a behaviour change hidden inside a
  translation is nearly impossible to find later.
- Where the F# leans on a language feature C# lacks (structural equality, exhaustive
  matching over a DU), pick the C# shape deliberately and write down why in a comment. A
  DU over load simulations, for instance, is a sealed hierarchy or a discriminated record
  with an enum tag — and the exhaustiveness the compiler used to guarantee now has to come
  from tests.
- Take out cluster seams as you pass them (see **Out of scope**).

## Testing

`tests/NBomber.IntegrationTests` is xUnit + FsCheck + Unquote today; ported tests are C#
xUnit, with FsCheck's C# API or another property-based library where a property test earns
its keep. Most tests really do run short load tests in-process. That makes them slow and slightly timing-sensitive:
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
