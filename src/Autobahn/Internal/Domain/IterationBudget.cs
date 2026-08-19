namespace Autobahn.Internal.Domain;

/// <summary>
/// The remaining iteration allowance of a counted load simulation.
/// </summary>
/// <remarks>
/// Shared between the scheduler, which decides how many copies to inject, and the actors,
/// which each claim one iteration before running it. Claiming up front rather than counting
/// afterwards is what keeps the total exact: a scenario asked for 100 iterations runs 100,
/// not "100 plus however many were already in flight when the hundredth finished".
/// </remarks>
internal sealed class IterationBudget(int total)
{
    private long _unclaimed = total;
    private long _completed;

    public int Total { get; } = total;

    /// <summary>How many iterations have run to completion.</summary>
    public long Completed => Interlocked.Read(ref _completed);

    /// <summary>True once every iteration has been claimed, whether or not it has finished.</summary>
    public bool FullyClaimed => Interlocked.Read(ref _unclaimed) <= 0;

    /// <summary>True once every iteration has finished. This is what ends the segment.</summary>
    public bool IsFinished => Completed >= Total;

    /// <summary>How many more copies it is worth injecting right now.</summary>
    public int RemainingToClaim => (int)Math.Max(0, Interlocked.Read(ref _unclaimed));

    /// <summary>Takes one iteration from the allowance, or returns false when there are none left.</summary>
    public bool TryClaim() => Interlocked.Decrement(ref _unclaimed) >= 0;

    public void MarkCompleted() => Interlocked.Increment(ref _completed);
}
