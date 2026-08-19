# Autobahn — Roadmap

This is the plan of record for Autobahn. It has three parts:

0. **The foundation** — turning the fork point into what Autobahn is meant to be: a pure
   C# library on .NET 10, with clustering removed. Done, apart from packaging and CI.
1. **Catch-up work** — capabilities, fixes and improvements that appeared in the upstream
   project between the 4.1.2 fork point and its current development line, captured here as
   *behaviour to build*.
2. **The Autobahn UI** — a Tesserae-based web interface hosted by the CLI over Kestrel.
   This is new work with no upstream equivalent.

### The three directional decisions

Everything in this document assumes these, and they are not up for re-litigation item by
item:

- **Autobahn is a pure C# library.** The engine is C#, there is one public API surface, and
  nothing in the dependency graph pulls in `FSharp.Core`.
- **Autobahn targets .NET 10.** Not `netstandard2.0`, not multi-targeting.
- **Clustering is removed.** Not deprecated, not left dormant — removed.

### How to read the catch-up part

Everything in part 1 describes **what a capability does and why it matters**, at the level
you would find in release notes or documentation. It deliberately contains no upstream
implementation detail, no APIs copied verbatim, and no source. Upstream releases after
4.1.2 are not under a license this project can draw from, so every item is a specification
to be designed and implemented independently.

Sequencing: the port is done and the hot paths have benchmark coverage, so nothing here is
blocked on the foundation any more. Within the rest, metrics (section 1) is what thresholds
(section 2) measures against, and both feed the reporting surfaces (section 5) and the UI
(section 8) — so they come first.

### Explicitly out of scope

**Clustering.** Coordinators, agents, distributed execution, cluster autoscaling, cluster
monitoring and everything that hangs off them are not part of Autobahn and appear nowhere
in the feature lists below. Autobahn is a single-process load generator. The cluster code
that came with the fork point is gone — see section 0.1.

**Real-time reporting sinks.** The fork point let you register an `IReportingSink` and
pushed interval statistics at it, which is how it fed time-series databases while a test
ran. That contract, its plumbing and the packages that would have implemented it are out of
scope: the way to watch a run as it happens is the web UI in section 8, and the way to get
a run's numbers into another system is the machine-readable run artifact in section 5. The
interface and its registration API have been removed rather than left dormant.

---

## 0. Foundation: the C# port, .NET 10, de-clustering

This section was the prerequisite for the rest, and it is done apart from the packaging
and CI items in 0.5. It is kept rather than deleted because
what was decided here — and what was deliberately *not* changed — is what the sections
below build on.

### 0.1 Remove clustering — done

Done as part of the C# port: `AddFromAgent` and the agent-stats buffers are gone from the
stats actor, the scheduler no longer takes a cluster count, `NodeType`, `ScenarioPartition`
and `TestInfo.ClusterId` no longer exist, and `NodeStats`/`NodeInfo`/`NodeSessionResult`
became `SessionStats`/`HostInfo`/`SessionResult`. Nothing that survived needed a "why it
survived" comment.

- [x] **Delete the cluster seams in the engine.** The agent-stats intake on the stats actor,
  the per-scenario cluster-count lookup in the test host, the coordinator/agent node types
  and the operation states that only exist for them.
- [x] **Simplify the stats pipeline to single-node.** The merge-shaped paths in the stats
  actor and statistics module exist to combine results across nodes. With one node they are
  pure overhead on the hot path and pure complexity in the port. Collapse them.
- [x] **Strip cluster configuration.** Cluster sections in the JSON config model, the
  matching CLI arguments, and their validation.
- [x] **Purge the vocabulary.** Node/coordinator/agent naming in types, stats records,
  reports and log messages, where it exists only because of clustering. Some of it is
  legitimately about "the machine this ran on" — keep that, rename the rest.
- [x] **Prove the removal.** The full test suite passes with the cluster code gone, and no
  public type refers to it. Anything that cannot be removed without breaking single-node
  behaviour gets a comment saying why it survived.

### 0.2 Vendor the contracts — done

- [x] **Bring the contract types into the repository as C# source.** The engine currently
  depends on an external, version-pinned, F# contracts package that this fork neither
  controls nor can evolve — it blocks the rename, the C# port and nearly every feature
  item below. Reimplement the contract surface (scenario context, response, stats records,
  plugin interface, run/test info) as C# source in this repository, matching the existing
  behaviour. Done as part of the port rather than as a separate project: the contract types
  live under `src/Autobahn/Contracts` and `src/Autobahn/Stats`.

### 0.3 Rewrite the engine in C# — done

The whole engine is C#. What is left of this section is the benchmark work, called out below.

- [x] **Agree the C# shape for the F# constructs that carry the design.** Settled as:
  - Discriminated unions (load simulations, scheduler commands, actor messages, errors) →
    sealed hierarchies or tagged records. Exhaustiveness stops being a compiler guarantee,
    so it becomes a test.
  - `Result`/`taskResult` validation pipelines → a small result type owned by this
    repository, with no F# dependency.
  - Structural equality and immutable records → C# `record` types.
  - `inline` hot-path helpers → aggressive inlining where a benchmark justifies it, plain
    methods otherwise. Don't guess; the benchmarks exist.
- [x] **Port order, bottom-up so each layer landed on ported foundations:**
  1. Constants, configuration model, extensions/utilities.
  2. Errors and domain types.
  3. Load simulation validation and expansion.
  4. Statistics, raw measurement stats, the stats actor.
  5. Scenario, step, scenario context.
  6. The actor and actor pool.
  7. The three schedulers.
  8. Reports and the reporting manager.
  9. Test host, session runner, context merging.
  10. The public API — collapsing the F#/C#/shared triple into one surface.
  11. Plugins, then the examples.
- [x] **Behaviour parity is the acceptance criterion.** Each ported file lands with its
  existing tests passing unchanged. Where a test had to change, the change is justified in
  the commit message — a silent behaviour change inside a translation is nearly impossible
  to find afterwards.
- [x] **Guard the hot paths with benchmarks.** `performance/Autobahn.Benchmarks` covers
  publishing a measurement, folding one into the tally, closing a reporting interval,
  every scheduling decision and building a load plan. The claim the port made is now
  measured: **publishing a measurement and accumulating it both allocate zero bytes**, so
  the struct mailbox message does what it was introduced to do. Baseline numbers are in
  that project's README; record them before touching the scheduler or the stats actor.
- [x] **Port the tests to C#.** On TUnit. The FsCheck properties became explicit
  `[Arguments]` cases plus seeded random sweeps rather than a new property-testing
  dependency; `LoadSimulationExhaustivenessTests` replaces the exhaustiveness the F#
  compiler used to prove over the load-simulation union.
- [x] **Delete the F# projects and the FSharp.Core dependency.** Nothing in the dependency
  graph references `FSharp.Core` any more. Two packages went with it: the F# markdown
  builder (replaced by `MarkdownDocument`) and the Serilog Spectre sink (logging is now
  Microsoft.Extensions.Logging with ZLogger). `ConsoleTables` went too, for a different
  reason — its alternative-style renderer throws.

### 0.4 Move to .NET 10

- [x] **Retarget everything to .NET 10** — engine, tests, examples, benchmarks — and drop
  `netstandard2.0`. Single target framework.
- [ ] **Use what that unlocks**, deliberately and where it pays. Done so far:
  `System.Threading.Channels` for the stats mailbox, `System.Text.Json` for config and the
  report view model, collection expressions and records throughout. Still owed: spans and
  `Memory<T>` on the measurement path where a benchmark justifies it, `ValueTask` on hot
  paths that usually complete synchronously, `TimeProvider` so tests can control time
  instead of sleeping (the suite currently spends minutes of wall clock on `Task.Delay`),
  `System.Text.Json` source generation, and the current `System.Diagnostics.Metrics`
  primitives as the substrate for section 1 rather than a hand-rolled equivalent.
- [x] **Set runtime configuration properly** in the shipped projects — server GC and
  concurrent GC, and `GCLatencyMode.SustainedLowLatency` for the duration of a run, so the
  generator does not report its own gen2 pause as the target's latency.
- [ ] **Confirm the thread-pool story.** A load generator's own scheduling is its most
  common self-inflicted bottleneck; document what Autobahn assumes and what it configures.

### 0.5 Repository and release

- [x] **Rename to Autobahn.** Namespaces, assembly, package id, entry-point types and
  config file names all moved with the port. No compatibility shim was shipped: the API
  changed shape at the same time (one surface instead of three, `SessionStats` instead of
  `NodeStats`, no reporting sinks), so aliases would have pointed at types that no longer
  mean the same thing.
- [x] **Package identity and metadata.** Authors, description, repository URL, icon, tags,
  license expression (`Apache-2.0`), symbols and deterministic builds, declared once in
  `Directory.Build.props`. Release notes are still owed.
- [x] **Retire the legacy build script.** The inherited Cake pipeline is deleted.
- [x] **A plain `dotnet build` at the root works, and stays working.** One solution file at
  the root (`Autobahn.slnx`) holding the engine, the CLI and the tests, so `dotnet build`
  and `dotnet test` need no arguments. The examples and the web UI live in their own
  solutions, out of the default build.
- [ ] **Grow the examples back.** The three inherited examples were deleted: they depended
  on upstream `NBomber.Http`, `NBomber.Data` and a sink package, none of which this fork
  has. `examples/HelloWorld` replaces them and builds against the local project. More are
  owed as the features they would demonstrate land — and once there is a set worth gating,
  fold `examples/Examples.slnx` into CI so they cannot rot unnoticed.
- [ ] **Packaging.** `dotnet pack` producing a correct package, plus a small script for the
  release steps. No build framework.
- [ ] **CI, from scratch.** The inherited GitHub Actions workflows were deleted, not
  parked: they built a solution that no longer exists, pinned an old SDK, and published to
  NuGet under the upstream package identity. Write the replacement: build and test on push
  and PR to `main`, publish on tag rather than on every push. One target framework means no
  matrix — keep it that way. Two things it has to get right that the old one did not: the
  test project runs on Microsoft.Testing.Platform (`dotnet test` needs the `global.json`
  runner opt-in), and the full suite spends minutes of wall clock in `Task.Delay`, so the
  gate should run the `Category!=slow` subset on PRs and everything on `main`.
- [x] **Dependency sweep.** The engine is down to eight packages: HdrHistogram,
  Spectre.Console, ZLogger, and five Microsoft.Extensions ones (Configuration plus its JSON
  and Binder providers, Logging plus its Configuration provider). Gone: FSharp.Core,
  FsToolkit.ErrorHandling, FSharp.Json, FuncyDown, CommandLineParser, ConsoleTables,
  Serilog and its four sinks/enrichers, and the external `NBomber.Contracts` package.
  Automated vulnerability scanning still needs somewhere to run — see CI, above.
- [x] **Repository hygiene.** File-scoped namespaces, nullable reference types enabled
  solution-wide, and a `Directory.Build.props` that declares the target framework, the
  language level and the package metadata once instead of per project. The
  `.editorconfig`-driven format check still needs CI to run in.
- [x] **Keep and extend the test suite.** The upstream development line dropped its
  integration tests. Autobahn kept them, ported them to C#/TUnit, and added coverage the
  fork point did not have (load-simulation exhaustiveness, config parsing and rejection,
  report content). Every item below still lands with tests.

---

## 1. Metrics

The single biggest gap. 4.1.2 measures latency, throughput, status codes and data transfer
per step; it has no notion of a *metric* that is neither of those.

- [ ] **A metrics subsystem alongside the existing stats pipeline.** A second, independent
  accumulator that collects named numeric series over the run, aggregated per reporting
  interval and over the whole session, and flushed through the same path that already
  feeds the console and the reports.
- [ ] **Metric kinds.** At minimum: *counter* (a value that moves up and down over the run),
  *gauge* (the current value, last write wins), and *histogram* (a distribution, reported
  with percentiles). Each metric carries a name, a unit of measure for display, and a
  scaling factor so a raw value (bytes) can be reported in a readable unit (MB).
- [ ] **Built-in runtime metrics.** Collect process and runtime health during the run
  without the user asking: CPU usage, working set, GC heap size, thread pool queue length
  and thread count, and bytes sent/received at the socket level. These are what turn "the
  target got slower" into "the load generator ran out of thread pool" — a load test that
  cannot prove it was not itself the bottleneck is not evidence.
  - [ ] Sample on a fixed interval, independent of the reporting interval, and aggregate.
  - [ ] Make the collector's own cost negligible and prove it with a benchmark.
  - [ ] Degrade gracefully where a counter is unavailable on a platform, rather than failing
        the run.
- [ ] **User-defined metrics.** Let a scenario create a counter or gauge, register it during
  scenario init, write to it from scenario or step code with negligible overhead, and read
  the final value off the run result afterwards. This is how someone tracks queue depth,
  cache hit ratio, or a business counter next to their latency numbers.
- [ ] **Stable, deterministic ordering of metric names** in every output, so a diff between
  two runs is a diff of values and not of row order.
- [ ] **Metrics in every output surface.** Console live table, txt/csv/md/html reports, and
  the run artifact (section 5) that the web UI and run-to-run comparison read.

## 2. Thresholds (pass/fail criteria)

4.1.2 can tell you what happened; it cannot tell you whether it was acceptable. Thresholds
are what make a load test usable as a CI gate.

- [ ] **Runtime thresholds evaluated during the run**, on every reporting interval, not only
  at the end. A threshold is a predicate over the current stats.
- [ ] **Scope.** Scenario-level (overall error rate, percentiles, throughput), step-level
  (the same, for one named step), status-code level (a given code's count or share), and
  metric-level (over the metrics from section 1).
- [ ] **Abort policy.** A threshold can be advisory (recorded, reported, fails the run at the
  end) or can abort the run once it has been violated N consecutive checks — the difference
  between "the report says it was bad" and "stop hammering a service that is already down".
- [ ] **Delayed start.** A threshold can be told to start checking only after a given elapsed
  time, so ramp-up noise does not trip a steady-state rule.
- [ ] **Declarative thresholds in the JSON config**, so the same test binary can be gated
  differently per environment without a recompile.
- [ ] **Reporting.** A threshold section in the reports and on the console showing each rule,
  its target, its observed value, and when it first failed.
- [ ] **Process exit code.** A failed threshold must produce a non-zero exit code and a
  clearly failed run result, or the CI gate is decorative. Make the exit-code contract
  explicit and documented.

## 3. Load model and scheduling

- [ ] **Scenario weight.** When several scenarios model one user population, let each declare
  a share of the traffic (e.g. 80% read / 20% write) rather than forcing the author to
  hand-compute rates per scenario. Weights apply to the combined load model and must remain
  correct while the load ramps.
- [ ] **Workload distribution helpers.** Ready-made ways to pick *which* work an iteration
  does, so a scenario can model realistic access patterns instead of uniform-random-only:
  uniform, Zipfian (a hot minority of keys — the realistic default for caches and content),
  and multinomial (explicit weighted choice between named operations).
- [ ] **Instance-aware distribution.** Expose the scenario copy's own index and the total copy
  count to user code, so a scenario can deterministically partition a dataset across copies
  (copy 7 of 100 takes rows 7, 107, 207…) instead of having every copy fight over the same
  rows.
- [ ] **Iteration-count simulations.** Run exactly N iterations — total, or N per injection
  step — instead of running for a duration. This is what makes a load test usable as a
  functional smoke test and makes small runs reproducible.
- [ ] **Correct duration accounting around pauses.** Time spent in a pause simulation must be
  excluded from the executed duration used to compute throughput, or every plan containing
  a pause under-reports RPS.
- [ ] **Scheduler shutdown rework.** Stopping is currently a synchronous call that cannot wait
  properly. Make stop asynchronous and deterministic: cancel, dispose both actor schedulers,
  wait for in-flight iterations with a bounded timeout, and report how many were abandoned.
- [ ] **Load-plan validation.** Validate the whole plan up front with messages that name the
  scenario and the offending simulation. Specifically: a random-injection simulation whose
  minimum rate is not below its maximum is a configuration error and must be rejected rather
  than silently producing degenerate load. Every validation message must identify which
  scenario it came from — with several scenarios registered, an unattributed error is a
  guessing game.

## 4. Timeouts and lifecycle

- [ ] **Scenario completion timeout.** When the load plan ends, in-flight iterations are still
  running. Give the runner a configurable grace period to let them finish and be counted,
  after which they are abandoned. Without it, long-running iterations are silently lost from
  the final numbers.
- [ ] **Per-step and per-iteration timeouts**, with a timed-out attempt recorded as a distinct
  failure kind rather than as a generic error, so a report distinguishes "slow" from "broken".
- [ ] **Scenario completion hook.** A callback that fires when a scenario finishes, receiving
  that scenario's final stats — the place to push a result somewhere, tear down a fixture, or
  fail a build, without wrapping the whole runner.
- [ ] **Explicit iteration-restart semantics.** The choice of whether a failed step aborts the
  iteration or lets it continue is what makes retry-until-success loops expressible. Make the
  behaviour explicit, documented, and covered by tests.
- [ ] **Forcible stop.** A predictable, documented path to end a run immediately, with the
  partial results still written out.

## 5. Reporting

- [ ] **Fix live console rendering.** The live table is prone to flicker and duplicated
  redraws, particularly with several scenarios and a narrow terminal. Rework rendering to
  redraw in place, degrade to plain lines when the output is not a TTY (CI logs), and never
  interleave with the logger.
- [ ] **Reporting timer boundaries.** The first and last reporting intervals are currently
  truncated by fixed start/stop delays, so the first and last data points are not comparable
  to the rest. Separate the start delay from the stop delay and make both correct, so every
  emitted interval covers a full window.
- [ ] **Run duration** in the final report should be the longest scenario's duration, not
  whichever scenario happens to be first.
- [ ] **Metrics and thresholds sections** in every report format.
- [ ] **Replace the handwritten HTML report** with output generated by the same UI components
  as the live web interface (see section 8), so there is one visual language and one
  codebase for both.
- [ ] **Stop wiping the report folder.** A run empties its output folder before it starts.
  With the default per-session folder that is a no-op, but a pinned `WithReportFolder`
  points Autobahn at a directory it then deletes recursively on every run — which is a
  surprising amount of destruction for a load-test tool. Narrow it to removing only the
  artifacts Autobahn itself wrote last time, or stop cleaning at all and let reports
  accumulate under their timestamped names.
- [ ] **Machine-readable run artifact.** A stable, versioned JSON document containing the full
  run result. It is what the UI replays, what run-to-run comparison consumes, and what a CI
  system can assert against. Everything else (txt/csv/md/html) is a rendering of it.

## 6. Configuration, CLI and data

- [ ] **A real CLI.** Today the runner takes an argument array from user code. Autobahn should
  ship a proper command-line front end: pointing at a test assembly or script, selecting
  target scenarios, overriding config and infra-config paths, choosing report formats and
  output folder, setting log level, and controlling the web UI (section 8).
- [ ] **Config layering with provenance.** Code defaults, JSON config, infra config,
  environment variables and CLI flags all contribute. Define the precedence order, document
  it, and be able to show the effective merged configuration with the source of each value —
  "why is the warm-up 30 seconds" should be answerable without reading three files.
- [ ] **Custom settings.** Typed per-scenario custom settings from the config file, plus a
  global custom-settings section shared by all scenarios, so environment-specific values
  (URLs, credentials, dataset sizes) live in config rather than in code.
- [ ] **Script support.** Run a load test from a single C# script file with no project — the
  fastest possible path from "I want to hammer this endpoint" to results.
- [ ] **Data feeds.** The existing feed abstraction (circular, constant, random over CSV, JSON
  and in-memory sources) needs: batch feeds that hand an iteration a group of items rather
  than one, feeds that stream instead of loading a whole file into memory, and a clear story
  for what happens when a finite feed is exhausted mid-run.

## 7. Ecosystem: protocol helpers and export

These ship as separate packages in this repository so they version together with the engine.
None of them exists yet.

**Protocol helpers**

- [ ] **HTTP.** The single most-used integration and the one that needs the most care:
  a request builder, response validation hooks (including custom pass/fail rules per
  request), configurable per-request timeouts, correct payload size accounting that counts
  what actually went over the wire rather than just the visible body, status-code capture,
  connection and handler reuse with explicit control over pooling, per-virtual-user cookie
  and session handling, and an opt-in request/response tracing mode for debugging a test.
- [ ] **WebSockets** with a client pool, covering both request/response and
  publish-then-consume patterns.
- [ ] **gRPC**, unary and streaming.
- [ ] **Message brokers** — MQTT and AMQP — supporting both the pooled-client shape (each
  virtual user owns a connection) and the independent-actors shape (separate publisher and
  consumer scenarios measuring end-to-end delivery latency).
- [ ] **Browser-driven testing.** Drive real browsers to measure what a user experiences,
  not just what the server returns. Needs a deliberate design for parallelism and resource
  ceilings — browsers are orders of magnitude heavier than an HTTP client and will happily
  make the load generator the bottleneck.
- [ ] **Traffic-capture conversion.** Turn a recorded browser session (HAR) into a starting
  scenario, so a realistic test does not start from a blank file.

**Getting a run's numbers somewhere else**

Reporting sinks are out of scope (see *Explicitly out of scope*). The database-backed ones
the fork point pointed at — InfluxDB, TimescaleDB, Datadog, Loki, Elasticsearch — are not
coming back in any form. What replaces them:

- [ ] **OpenTelemetry (OTLP) export** of stats and metrics. The one integration worth
  building, because it reaches every backend the user already runs instead of adding
  another; and because it is a *push at the end plus per-interval*, not a plugin contract
  that user code implements.
- [ ] **Everything else goes through the run artifact** (section 5): a stable, versioned
  JSON document that a CI job, a dashboard importer or a comparison tool reads. One format
  to keep stable rather than a family of sink packages to keep building.

**Logging** is already `Microsoft.Extensions.Logging` with ZLogger providers, so a user who
wants their run's log in Loki, Elasticsearch or anywhere else adds the provider they
already use — nothing to build here.

---

## 8. The Autobahn UI

A live web interface for a running load test, written in C# with
[Tesserae](https://github.com/curiosity-ai/tesserae) and compiled to JavaScript by
Transpose, served by the Autobahn CLI over Kestrel.

### Why

A load test produces a firehose of numbers whose *shape over time* is the whole point: the
knee in the latency curve, the moment errors start, whether throughput plateaus or collapses.
A console table shows one instant and a static HTML report shows the corpse. The people who
actually need to watch a load run — and the people they need to show it to while it is
running — need a live picture.

Writing it in Tesserae means the UI is C#, shares DTOs with the host by project reference
rather than by hand-written JSON contracts on both sides, has no npm toolchain, and compiles
to static assets that can be embedded in the CLI assembly and served with no network access.

### Shape of the thing

Three new projects:

- **`Autobahn.Ui.Contracts`** — the wire DTOs: run descriptor, scenario/step snapshots,
  interval frames, metric series, threshold states, log entries, control commands.
  Referenced by both the host and the UI, so the wire format is checked by the compiler on
  both ends and cannot drift.

  **This one project is the deliberate exception to the .NET 10 rule**, but a narrower
  exception than it first looks. Tesserae and the Transpose compiler build against
  `netstandard2.0`, so a project the UI references has to target it too — alone, or
  multi-targeted with .NET 10 for the host's benefit.

  The *language* is not the constraint: Transpose supports `LangVersion latest`, so
  records, pattern matching, init-only properties and file-scoped namespaces are all
  available and the DTOs can read like modern C#. What stays off the table is what the
  target framework and the transpiled BCL cannot provide — .NET-10-only APIs, generic math,
  span-flavoured overloads, source generators. Two things to settle when the project is
  created: whether `record` and `init` need an `IsExternalInit` shim at `netstandard2.0`,
  and which BCL types Transpose actually implements for the shapes being serialised. Keep
  the DTOs plain regardless — this is a schema both ends have to agree on, not a place for
  clever C#. Everything else in the repository is .NET 10 only.
- **`Autobahn.Ui`** — the Tesserae app, C# compiled to JS/CSS/HTML by Transpose at build
  time; the output is embedded into the CLI assembly as resources. Also `netstandard2.0`,
  for the same reason.
- **`Autobahn.Cli`** — .NET 10. A dotnet tool that runs a test and hosts the UI.

### Hosting

- Kestrel, started by the CLI in-process alongside the test run, off by default in CI and on
  by default for an interactive terminal. Flags: enable/disable, port (0 = pick a free one),
  bind address, and open-browser-on-start.
- **Binds to loopback by default.** Exposing it on another interface requires an explicit
  flag and prints a warning: a load-test control surface that can stop the run is not
  something to put on 0.0.0.0 by accident.
- **A per-run access token** in the URL the CLI prints, required by every endpoint. Control
  actions (stop the run) additionally require an explicit confirmation from the UI.
- All assets served from embedded resources with a strict CSP and no external requests —
  fonts, icons and scripts included. It must work on an air-gapped build agent.
- **The UI must never affect the run.** No client connected, twenty clients connected, a
  client on a slow link, the browser tab closed mid-run: the load test's timing, results and
  exit code are identical. Snapshot production happens once per reporting interval regardless
  of who is watching; delivery is best-effort per client with a bounded per-client queue that
  drops frames rather than applying back-pressure to the engine.

### Transport

- `GET /api/run` — the run descriptor: test suite and name, scenario list with their load
  plans, reporting interval, node info, config provenance, start time, planned duration.
- `GET /api/snapshot` — the current full state, so a page loaded mid-run renders immediately.
- `GET /api/history?from=` — backfill of past interval frames from a bounded ring buffer the
  host keeps, so a late viewer's charts are not empty.
- `WS /api/live` — one frame per reporting interval: per-scenario and per-step deltas, metric
  values, threshold states, recent log lines. Frames are numbered so a client can detect a gap
  and re-request from `/api/history`.
- `POST /api/control/stop` — graceful stop; `POST /api/control/stop?force=true` — immediate.
- `GET /api/reports` — the artifacts produced so far, downloadable.
- Reconnect with exponential backoff and gap recovery on the client. A dropped WebSocket must
  heal itself without a page reload.

### Screens

**1. Live dashboard** — the default view.

- Header: test suite/name, elapsed vs planned with a progress bar, the currently executing
  load simulation, node info, and a run-state pill (init / warm-up / bombing / stopping /
  finished / failed). A `LiveProgress` line carries the current status text.
- KPI row of `Metric` tiles with `Sparkline` insets: requests/sec, total ok, total failed,
  error %, p50/p95/p99 latency, data sent/received. Each tile shows the delta against the
  previous interval so a trend is visible without reading the chart.
- Primary `LineChart`: throughput over time, with failed requests as a second series.
- Secondary `LineChart`: latency percentiles over time (p50/p75/p95/p99), fitted rather than
  zero-based so the interesting range is legible.
- Load chart: scheduled vs actual concurrency and injection rate, overlaid. Divergence between
  scheduled and actual is the clearest signal that the *generator* is saturated, and it should
  be visually obvious.
- System charts, from the runtime metrics of section 1: CPU, memory and GC heap, thread pool
  queue, socket bytes in/out.
- Status-code breakdown as a stacked `BarChart` over time plus a table of counts and shares.
- A collapsible live log tail (virtualized) with level filtering.

**2. Scenarios** — a `Pivot` with one tab per scenario.

- That scenario's own KPI row and charts, scoped to it.
- A `DetailsList` of steps: name, ok/fail counts, error %, latency percentiles, data transfer,
  and a per-row sparkline of the step's latency. Sortable, and clickable through to a step
  detail panel with the full latency distribution as a histogram.
- The scenario's load plan and where it currently is in it.

**3. Errors**

- Failures grouped by status code and by error message, with count, share of total, first and
  last seen, and which step and scenario they came from. Expandable to sample instances.
- A timeline strip showing when each error group was active — a burst of errors confined to
  one 30-second window is a different problem from a steady 2% error rate, and the report
  should not flatten one into the other.

**4. Thresholds**

- One row per threshold: what it asserts, its target, its current value, pass/fail, whether it
  will abort the run, and when its checking window started.
- An `UptimeBars` strip per threshold showing pass/fail for every reporting interval so far —
  a threshold that passed, failed for a minute, and recovered reads at a glance.
- The overall gate verdict, prominently, because that is the number CI will act on.

**5. Load plan**

- A timeline per scenario showing the sequence of load simulations as labelled segments —
  ramp, hold, inject, pause — with a playhead at the current position and the projected
  concurrency/rate curve drawn over it.
- Rendered before the run starts too, so the plan can be sanity-checked before firing.

**6. Configuration**

- The effective merged configuration, read-only, with the source of each value (code default,
  JSON config, infra config, environment variable, CLI flag). Directly serves the "why is this
  value what it is" question from section 6.

**7. Runs and comparison**

- A list of previous runs found in the report folder, opened from their machine-readable run
  artifacts (section 5).
- **Compare two runs**: the same charts with both runs as series, and a table of deltas per
  scenario and per step with the change highlighted. This is the feature that turns Autobahn
  from "how fast is it" into "did this commit make it slower", and it should be a first-class
  screen rather than an afterthought.

**8. Reports** — the generated artifacts, with download links and an inline preview.

### Interaction and presentation

- App shell: a `Sidenav` rail for the sections, content on the right. Light/dark following the
  Tesserae theme and the OS preference.
- Charts are bound to observables fed by the WebSocket, so an incoming frame appends a point
  and re-renders in place rather than rebuilding the component tree. At a 5-second reporting
  interval, an hour is 720 points per series — fine — but the host downsamples older data for
  long runs so a page opened into hour six is not asked to draw 4,000 points per series.
- Every list that can grow without bound (logs, errors, per-step rows) is virtualized.
- Time axis is elapsed-since-start by default, with a toggle to wall-clock time.
- Keyboard: number keys jump between sections, `/` focuses search, `.` pauses live updates so
  a chart can be read without it moving under the cursor.
- Fully responsive down to a phone — watching a long run from somewhere other than the desk
  that started it is a real use case.

### Static export

`autobahn ui export <run-artifact>` renders the same application against a static run
artifact embedded in a single self-contained HTML file: same components, same charts, no
server. This replaces the current handwritten HTML report and means the end-of-run artifact
and the live view can never drift apart.

### Milestones

1. Contracts project, host skeleton with Kestrel and embedded assets, `/api/run` and a live
   WebSocket carrying raw interval frames.
2. Live dashboard: KPI tiles, throughput and latency charts, status codes, log tail.
3. Scenario and step detail; errors screen.
4. Metrics and thresholds screens (depends on sections 1 and 2).
5. Load plan and configuration screens.
6. Run history and run-to-run comparison.
7. Static export, and retirement of the handwritten HTML report.
