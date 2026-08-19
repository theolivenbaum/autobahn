namespace Autobahn.Feeds;

/// <summary>What a finite feed does when it runs out of items mid-run.</summary>
/// <remarks>
/// The fork point left this undecided, which meant a run that outlasted its data quietly
/// started repeating it - and a test whose whole point was "each user is distinct" silently
/// stopped being that test. Every finite feed here states which of these it is.
/// </remarks>
public enum FeedExhaustion
{
    /// <summary>Start again from the beginning. The default for a circular feed.</summary>
    Restart,

    /// <summary>
    /// Throw <see cref="FeedExhaustedException"/>, which fails the iteration that asked. Use
    /// this when repeating the data would invalidate the test.
    /// </summary>
    Fail,

    /// <summary>
    /// Stop the scenario cleanly. The run finishes with whatever it measured, which is what
    /// you want when the dataset *is* the workload.
    /// </summary>
    StopScenario
}

/// <summary>Thrown when a finite feed runs out and its policy says to fail rather than repeat.</summary>
public sealed class FeedExhaustedException(string feedName, int itemCount)
    : AutobahnException(
        $"Feed '{feedName}' handed out all {itemCount} of its items and is set to fail rather than "
        + "repeat them. Give it more data, shorten the load plan, or change its exhaustion policy.")
{
    public string FeedName { get; } = feedName;
    public int ItemCount { get; } = itemCount;
}
