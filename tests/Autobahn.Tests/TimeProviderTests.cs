using System.Diagnostics;
using Autobahn.Stats;
using Microsoft.Extensions.Time.Testing;

namespace Autobahn.Tests;

/// <summary>
/// The engine schedules on <see cref="TimeProvider"/> and measures on
/// <see cref="Stopwatch"/>, and these are the two halves of that split.
/// </summary>
/// <remarks>
/// The scheduling half is what makes a ten-second plan finish in a fraction of a second:
/// every wait the engine does itself - the reporting tick, the gap between simulation
/// intervals, the start jitter, the shutdown poll - comes off the clock the run was handed.
/// The measuring half is why the numbers a faked run reports are still real: latency is read
/// from a Stopwatch that nothing can move, so a faked clock changes when a run does things
/// and never what it claims to have observed.
/// </remarks>
internal class TimeProviderTests
{
    /// <summary>
    /// Drives a run forward on a fake clock, returning once it finishes or the real-world
    /// budget runs out.
    /// </summary>
    /// <remarks>
    /// The advancing has to happen from outside the run, because the run is blocking whatever
    /// thread called it. The yield between steps is what lets the continuations a step
    /// released actually make progress before the next one lands on top of them.
    /// </remarks>
    private static async Task<SessionResult> DriveOnFakeClock(
        FakeTimeProvider clock, Task<SessionResult> run, TimeSpan step, TimeSpan realBudget)
    {
        var realTime = Stopwatch.StartNew();

        while (!run.IsCompleted && realTime.Elapsed < realBudget)
        {
            clock.Advance(step);
            await Task.Delay(1).ConfigureAwait(false);
        }

        return await run.ConfigureAwait(false);
    }

    [Test]
    [NotInParallel]
    public async Task A_session_runs_on_the_clock_it_was_handed()
    {
        var clock = new FakeTimeProvider();
        var iterations = 0;

        var scenario = Scenario
            .Create("faked", _ =>
            {
                Interlocked.Increment(ref iterations);
                return Task.FromResult<IResponse>(Response.Ok());
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.Inject(rate: 10, interval: Time.Seconds(1), during: Time.Seconds(30)));

        var realTime = Stopwatch.StartNew();

        var run = Task.Run(() => AutobahnRunner
            .RegisterScenarios(scenario)
            .WithTestName("faked clock")
            .WithReportingInterval(Time.Seconds(5))
            .WithoutReports()
            .WithoutCancelKeyPress()
            .WithTimeProvider(clock)
            .RunWithResult());

        var result = await DriveOnFakeClock(
            clock, run, step: TimeSpan.FromMilliseconds(100), realBudget: TimeSpan.FromSeconds(60));

        realTime.Stop();

        // Thirty seconds of plan, in a fraction of that. The point of the seam.
        await Assert.That(realTime.Elapsed).IsLessThan(TimeSpan.FromSeconds(30));

        // And it really ran the plan rather than skipping it: an open-model injection of 10 a
        // second for 30 seconds is 300 iterations, and every one of them succeeded.
        await Assert.That(iterations).IsEqualTo(300);

        var stats = result.FinalStats.ScenarioStats[0];
        await Assert.That(stats.Ok.Request.Count).IsEqualTo(300);
        await Assert.That(stats.Fail.Request.Count).IsEqualTo(0);
    }

    [Test]
    [NotInParallel]
    public async Task The_timeline_fills_in_on_a_fake_clock_too()
    {
        var clock = new FakeTimeProvider();

        var scenario = Scenario
            .Create("timeline", _ => Task.FromResult<IResponse>(Response.Ok()))
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.Inject(rate: 5, interval: Time.Seconds(1), during: Time.Seconds(20)));

        var run = Task.Run(() => AutobahnRunner
            .RegisterScenarios(scenario)
            .WithTestName("faked timeline")
            .WithReportingInterval(Time.Seconds(5))
            .WithoutReports()
            .WithoutCancelKeyPress()
            .WithTimeProvider(clock)
            .RunWithResult());

        var result = await DriveOnFakeClock(
            clock, run, step: TimeSpan.FromMilliseconds(100), realBudget: TimeSpan.FromSeconds(60));

        // The reporting timer is on the same clock, so twenty seconds of plan at a five-second
        // interval is four closed intervals - not the zero a real timer would have managed in
        // the wall-clock time this took.
        await Assert.That(result.TimeLineHistory.Count).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task The_default_clock_is_the_system_one()
    {
        await Assert.That(AutobahnContext.Empty.TimeProvider).IsSameReferenceAs(TimeProvider.System);

        var replaced = AutobahnContext.Empty.WithTimeProvider(new FakeTimeProvider());
        await Assert.That(replaced.TimeProvider).IsNotSameReferenceAs(TimeProvider.System);
    }

    [Test]
    [NotInParallel]
    public async Task Latency_is_measured_on_a_clock_the_fake_one_cannot_move()
    {
        var clock = new FakeTimeProvider();

        // The scenario sleeps for real. A run whose measuring clock had been faked along with
        // its scheduling clock would report this as instant.
        var scenario = Scenario
            .Create("measured", async _ =>
            {
                await Task.Delay(25).ConfigureAwait(false);
                return Response.Ok();
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.Inject(rate: 2, interval: Time.Seconds(1), during: Time.Seconds(5)));

        var run = Task.Run(() => AutobahnRunner
            .RegisterScenarios(scenario)
            .WithTestName("faked but measured")
            .WithReportingInterval(Time.Seconds(5))
            .WithoutReports()
            .WithoutCancelKeyPress()
            .WithTimeProvider(clock)
            .RunWithResult());

        var result = await DriveOnFakeClock(
            clock, run, step: TimeSpan.FromMilliseconds(100), realBudget: TimeSpan.FromSeconds(60));

        var stats = result.FinalStats.ScenarioStats[0];

        await Assert.That(stats.Ok.Request.Count).IsGreaterThan(0);
        await Assert.That(stats.Ok.Latency.MinMs).IsGreaterThanOrEqualTo(20);
    }
}
