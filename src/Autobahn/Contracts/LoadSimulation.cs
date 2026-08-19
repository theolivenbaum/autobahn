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

    /// <summary>Runs no load at all for a while.</summary>
    public sealed record Pause(TimeSpan During) : LoadSimulation;

    /// <summary>How long this segment lasts.</summary>
    public TimeSpan Duration => this switch
    {
        RampingConstant x => x.During,
        KeepConstant x    => x.During,
        RampingInject x   => x.During,
        Inject x          => x.During,
        InjectRandom x    => x.During,
        Pause x           => x.During,
        _ => throw new NotSupportedException($"Unknown load simulation: {GetType().Name}")
    };
}
