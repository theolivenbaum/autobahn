namespace Autobahn;

/// <summary>
/// Builds the load simulations a scenario's plan is made of. Simulations run one after
/// another, in the order they are given.
/// </summary>
public static class Simulation
{
    /// <summary>
    /// Ramps the number of running scenario copies linearly to <paramref name="copies"/>.
    /// Each copy is a long-running loop, destroyed when the simulation ends. Closed model:
    /// use it for anything with a client pool - databases, brokers, connection reuse.
    /// </summary>
    public static LoadSimulation RampingConstant(int copies, TimeSpan during) => new LoadSimulation.RampingConstant(copies, during);

    /// <summary>
    /// Keeps <paramref name="copies"/> scenario copies running for the whole duration, each
    /// executing as many iterations as it can. Closed model.
    /// </summary>
    public static LoadSimulation KeepConstant(int copies, TimeSpan during) => new LoadSimulation.KeepConstant(copies, during);

    /// <summary>
    /// Ramps the injection rate linearly to <paramref name="rate"/> copies per
    /// <paramref name="interval"/>. Each copy runs one iteration and is destroyed. Open
    /// model: the rate does not sag when the target slows down, which is what you want for HTTP.
    /// </summary>
    public static LoadSimulation RampingInject(int rate, TimeSpan interval, TimeSpan during) =>
        new LoadSimulation.RampingInject(rate, interval, during);

    /// <summary>
    /// Injects <paramref name="rate"/> scenario copies every <paramref name="interval"/>.
    /// Each copy runs one iteration and is destroyed. Open model.
    /// </summary>
    public static LoadSimulation Inject(int rate, TimeSpan interval, TimeSpan during) =>
        new LoadSimulation.Inject(rate, interval, during);

    /// <summary>
    /// Injects a random number of copies between <paramref name="minRate"/> and
    /// <paramref name="maxRate"/> every <paramref name="interval"/>. Open model.
    /// </summary>
    public static LoadSimulation InjectRandom(int minRate, int maxRate, TimeSpan interval, TimeSpan during) =>
        new LoadSimulation.InjectRandom(minRate, maxRate, interval, during);

    /// <summary>
    /// Keeps <paramref name="copies"/> scenario copies running until exactly
    /// <paramref name="iterations"/> iterations have completed, then moves on. Closed model.
    /// </summary>
    /// <remarks>
    /// Counted, not timed: this is what makes a load test usable as a functional smoke test,
    /// and what makes a small run reproducible. How long it takes is up to the target.
    /// </remarks>
    public static LoadSimulation IterationsForConstant(int copies, int iterations) =>
        new LoadSimulation.IterationsForConstant(copies, iterations);

    /// <summary>
    /// Injects <paramref name="rate"/> copies every <paramref name="interval"/> until exactly
    /// <paramref name="iterations"/> have been started, then moves on. Open model.
    /// </summary>
    public static LoadSimulation IterationsForInject(int rate, TimeSpan interval, int iterations) =>
        new LoadSimulation.IterationsForInject(rate, interval, iterations);

    /// <summary>Runs no load at all, for delaying a scenario's start or pausing mid-plan.</summary>
    public static LoadSimulation Pause(TimeSpan during) => new LoadSimulation.Pause(during);
}
