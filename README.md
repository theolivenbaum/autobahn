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

Early, and mid-transition. The code currently in this repository is the 4.1.2 fork point:
F#, targeting `netstandard2.0`, largely unmodified. Autobahn is being rewritten from that
starting point into a **pure C# library on .NET 10** — every F# file in the engine gets
ported, and the F#-specific parts of the public API go away. Clustering is being removed
outright.

The port, the rename, and the feature roadmap are tracked in [TODO.md](TODO.md), which is
the plan of record. Expect the API to move until the port lands.

## Why a fork

NBomber 4.1.2 is a small, sharp, well-factored load-testing engine, and it is the last
version of it that is free software. Autobahn keeps that engine open under Apache-2.0 and
takes it in its own direction:

- **Open, permanently.** Apache-2.0, no paid tiers, no feature gates, no license server.
- **Pure C#, current .NET.** One language across the engine, the API, the tests and the
  UI, on .NET 10. The original engine is F#; every line of it is being ported. That is a
  large, deliberate cost, paid once, so that the people most likely to contribute to a
  .NET load-testing tool can read and change every part of it — and so the engine can use
  what modern .NET actually offers.
- **Focused on the single-node engine.** Distributed/cluster execution is out of scope,
  and the cluster code inherited from the fork point is being removed rather than left to
  rot — see [TODO.md](TODO.md).
- **A real UI.** A first-class live web interface served by the CLI, not just a console
  table and a static HTML file at the end.
- **Batteries in the box.** Metrics, thresholds, and the common reporting sinks are part
  of the project rather than separate closed packages.

## Hello world

```csharp
using NBomber.CSharp;

var scenario = Scenario.Create("hello_world_scenario", async context =>
{
    // put any logic here: an HTTP call, a SQL query, a gRPC request.
    // Autobahn measures how long it takes and whether it succeeded.
    await Task.Delay(1_000);

    return Response.Ok();
})
.WithLoadSimulations(
    Simulation.Inject(rate: 10,
                      interval: TimeSpan.FromSeconds(1),
                      during: TimeSpan.FromSeconds(30))
);

NBomberRunner
    .RegisterScenarios(scenario)
    .Run();
```

> The public namespaces are still `NBomber.*` at the fork point, and `NBomber.CSharp` is
> the C#-facing half of an API that also has an F# half. After the port there is one
> surface, under `Autobahn.*`.

## Core concepts

| Concept | What it is |
|--|--|
| **Scenario** | One user journey. Runs in a loop, in parallel, for as long as the load model says. |
| **Step** | A named, measured slice inside a scenario, so one scenario can report several latencies. |
| **Load simulation** | The shape of the load over time: keep N copies constant, ramp them, inject at a fixed or random rate, or pause. Several compose into a plan. |
| **Response** | What a scenario or step returns: ok/fail, an optional payload, a status code, a size in bytes. |
| **Reporting sink** | Where real-time stats go while the test runs (InfluxDB, TimescaleDB, OTLP, your own). |
| **Worker plugin** | Background work that runs alongside the test and contributes its own stats (e.g. ping). |
| **Report** | The end-of-run artifact: txt, csv, md, html. |

## Load simulations

```csharp
.WithLoadSimulations(
    Simulation.RampingConstant(copies: 50, during: TimeSpan.FromSeconds(30)),
    Simulation.KeepConstant(copies: 50, during: TimeSpan.FromMinutes(5)),
    Simulation.RampingInject(rate: 100, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30)),
    Simulation.Inject(rate: 100, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromMinutes(5)),
    Simulation.InjectRandom(minRate: 50, maxRate: 100, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromMinutes(1)),
    Simulation.Pause(during: TimeSpan.FromSeconds(10))
)
```

Closed-model simulations (`RampingConstant`, `KeepConstant`) control **concurrency**: how
many copies of the scenario are alive. Open-model simulations (`RampingInject`, `Inject`,
`InjectRandom`) control **arrival rate**: how many iterations start per interval,
regardless of how many are still running. Reach for the open model when you are testing a
system's capacity, and the closed model when you are simulating a fixed population of
users.

## Building

You need the .NET 10 SDK. From the repository root:

```bash
dotnet build
dotnet test --filter CI!=disable
```

That is the whole story — no build script, no arguments, no bootstrapper. The
`CI!=disable` filter skips the tests that need long wall-clock time or external services.

The examples and the benchmarks have their own solutions and are not part of the root
build; build them explicitly if you want them:

```bash
dotnet build examples/Examples.slnx
dotnet build performance/Performance.slnx
```

## Repository layout

```
Autobahn.slnx         the root solution: the engine and its tests
src/NBomber/          the engine — F# today, being ported to C#
tests/                integration tests
examples/             runnable examples (own solution)
performance/          benchmarks (own solution)
assets/               images
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
