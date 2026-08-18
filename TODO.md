# Autobahn — Roadmap

This is the plan of record for Autobahn. It has two halves:

1. **Catch-up work** — capabilities, fixes and improvements that appeared in the upstream
   project between the 4.1.2 fork point and its current development line, captured here as
   *behaviour to build*, plus fork-specific housekeeping.
2. **The Autobahn UI** — a Tesserae-based web interface hosted by the CLI over Kestrel.
   This is new work with no upstream equivalent.

### How to read the catch-up half

Everything below describes **what a capability does and why it matters**, at the level you
would find in release notes or documentation. It deliberately contains no upstream
implementation detail, no APIs copied verbatim, and no source. Upstream releases after
4.1.2 are not under a license this project can draw from, so every item here is a
specification to be designed and implemented independently, not a port.

### Explicitly out of scope

**Clustering.** Coordinators, agents, distributed execution, cluster autoscaling, cluster
monitoring and everything that hangs off them are not part of Autobahn and are not listed
below. Autobahn is a single-process load generator. Where the 4.1.2 code still has
cluster-shaped seams, they are legacy and should be removed rather than extended.

---

## 0. Fork housekeeping

These come first because most of the rest depends on them.

- [ ] **Vendor the contracts.** The engine depends on an external, version-pinned contracts
  package that this fork does not control and cannot evolve. Bring the contract types
  (scenario context, response, stats records, sink and plugin interfaces, node/test info)
  into the repository as a source project so the public surface can change. This blocks
  almost every feature item below.
- [ ] **Rename to Autobahn.** Namespaces, assembly, package id, entry-point types, config
  file names and environment variables. Ship a compatibility shim package or type aliases
  for one release so an existing test suite can move over by changing a `using`.
- [ ] **Package identity and metadata.** Authors, description, repository URL, icon, tags,
  license expression (`Apache-2.0`), release notes, symbols and deterministic builds.
- [ ] **Replace the legacy build script.** The inherited Cake pipeline clones upstream
  plugin repositories and references projects that do not exist here. Replace with a plain
  `dotnet build` / `dotnet pack` pipeline plus a small script for the release steps.
- [ ] **CI.** Build and test on push and PR to `main`; matrix over supported .NET versions;
  publish on tag rather than on every push to `main`.
- [ ] **Modernise the target framework story.** Keep `netstandard2.0` for the widest reach
  or move to current LTS — decide deliberately and document it. Enable server GC and
  concurrent GC in the shipped projects, and make sure the examples inherit sane settings.
- [ ] **Dependency sweep.** Audit and update transitive dependencies, remove packages the
  engine no longer needs, and add automated vulnerability scanning to CI.
- [ ] **Repository hygiene.** Migrate the solution to the newer solution format, adopt
  file-scoped namespaces across the C# examples, and add an `.editorconfig`-driven format
  check to CI.
- [ ] **Keep and extend the test suite.** The upstream development line dropped its
  integration tests. Autobahn keeps them, and every item below lands with tests.

---

## 1. Metrics

The single biggest gap. 4.1.2 measures latency, throughput, status codes and data transfer
per step; it has no notion of a *metric* that is neither of those.

- [ ] **A metrics subsystem alongside the existing stats pipeline.** A second, independent
  accumulator that collects named numeric series over the run, aggregated per reporting
  interval and over the whole session, and flushed through the same path that already
  feeds the console, the reports and the real-time sinks.
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
  the real-time sink payload. The sink contract has to carry metrics, which is a breaking
  change to it — do it once, with the rename.

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

## 7. Ecosystem: protocols and sinks

These ship as separate packages in this repository so they version together with the engine.

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

**Real-time sinks and logging**

- [ ] Time-series sinks: InfluxDB (v1 and v2 line protocols), TimescaleDB/PostgreSQL.
- [ ] **OpenTelemetry (OTLP) export** of stats and metrics — the one that matters most,
  because it reaches every backend the user already runs instead of adding another.
- [ ] Datadog.
- [ ] Log sinks: rolling text file, Grafana Loki, Elasticsearch.
- [ ] A documented, tested path for writing a custom sink, including what is guaranteed
  about call ordering, threading, and failure handling (a sink that throws must never take
  the run down).
- [ ] Reference deployments (Docker Compose, Kubernetes) for each sink, kept building in CI.

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

- **`Autobahn.Ui.Contracts`** — `netstandard2.0`, no dependencies. The wire DTOs: run
  descriptor, scenario/step snapshots, interval frames, metric series, threshold states, log
  entries, control commands. Referenced by both the host and the UI, so the wire format is
  checked by the compiler on both ends and cannot drift.
- **`Autobahn.Ui`** — the Tesserae app. Compiled to JS/CSS/HTML at build time; the output is
  embedded into the CLI assembly as resources.
- **`Autobahn.Cli`** — a dotnet tool that runs a test and hosts the UI.

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
