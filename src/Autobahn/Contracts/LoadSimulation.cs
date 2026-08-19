namespace Autobahn;

/// <summary>
/// One segment of a scenario's load plan.
/// </summary>
/// <remarks>
/// This is a closed hierarchy: the base constructor is private, so the six nested records
/// below are the only cases that can ever exist. That is the C# shape chosen for what the
/// fork point expressed as an F# discriminated union. The compiler no longer proves that a
/// switch over the cases is exhaustive, so <c>SimulationPlan</c> switches end in a throwing
/// default arm and <c>LoadSimulationExhaustivenessTests</c> covers every case explicitly.
///
/// Build instances through the <see cref="Simulation"/> factory, which is the documented surface.
/// </remarks>
public abstract record LoadSimulation
{
    private LoadSimulation() { }

    /// <summary>Adds or removes scenario copies with a linear ramp. Closed model.</summary>
    public sealed record RampingConstant(int Copies, TimeSpan During) : LoadSimulation;

    /// <summary>Keeps a fixed number of scenario copies running. Closed model.</summary>
    public sealed record KeepConstant(int Copies, TimeSpan During) : LoadSimulation;

    /// <summary>Injects copies at a linearly ramping rate. Open model.</summary>
    public sealed record RampingInject(int Rate, TimeSpan Interval, TimeSpan During) : LoadSimulation;

    /// <summary>Injects copies at a fixed rate. Open model.</summary>
    public sealed record Inject(int Rate, TimeSpan Interval, TimeSpan During) : LoadSimulation;

    /// <summary>Injects copies at a random rate between two bounds. Open model.</summary>
    public sealed record InjectRandom(int MinRate, int MaxRate, TimeSpan Interval, TimeSpan During) : LoadSimulation;

    /// <summary>
    /// Keeps <paramref name="Copies"/> scenario copies running until <paramref name="Iterations"/>
    /// iterations have completed, however long that takes. Closed model.
    /// </summary>
    public sealed record IterationsForConstant(int Copies, int Iterations) : LoadSimulation;

    /// <summary>
    /// Injects copies at a fixed rate until <paramref name="Iterations"/> have been started.
    /// Open model.
    /// </summary>
    public sealed record IterationsForInject(int Rate, TimeSpan Interval, int Iterations) : LoadSimulation;

    /// <summary>Runs no load at all for a while.</summary>
    public sealed record Pause(TimeSpan During) : LoadSimulation;

    /// <summary>
    /// How long this segment lasts, or <see cref="TimeSpan.Zero"/> for the iteration-count
    /// simulations, whose length is decided by the target system rather than by the plan.
    /// </summary>
    public TimeSpan Duration => this switch
    {
        RampingConstant x       => x.During,
        KeepConstant x          => x.During,
        RampingInject x         => x.During,
        Inject x                => x.During,
        InjectRandom x          => x.During,
        Pause x                 => x.During,
        IterationsForConstant   => TimeSpan.Zero,
        IterationsForInject     => TimeSpan.Zero,
        _ => throw new NotSupportedException($"Unknown load simulation: {GetType().Name}")
    };

    /// <summary>
    /// How many iterations this segment runs, or null when it runs for a duration instead.
    /// </summary>
    public int? IterationCount => this switch
    {
        IterationsForConstant x => x.Iterations,
        IterationsForInject x   => x.Iterations,
        _ => null
    };
}
