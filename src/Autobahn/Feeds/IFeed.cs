namespace Autobahn.Feeds;

/// <summary>
/// Where a scenario gets the data one iteration works on.
/// </summary>
/// <remarks>
/// A feed is read from the hot path by every copy of a scenario at once, so an implementation
/// has to be thread-safe and has to be cheap. The built-in ones are both: they hand out items
/// with a single interlocked increment over an array they loaded once.
/// </remarks>
public interface IFeed<out T>
{
    /// <summary>What the reports and the errors call this feed.</summary>
    string FeedName { get; }

    /// <summary>
    /// The next item. Throws <see cref="FeedExhaustedException"/> when a finite feed has
    /// nothing left and its exhaustion policy says to stop.
    /// </summary>
    T Next();
}

/// <summary>A feed that hands an iteration a group of items rather than one.</summary>
public interface IBatchFeed<out T> : IFeed<IReadOnlyList<T>>
{
    /// <summary>How many items each call to <see cref="IFeed{T}.Next"/> returns.</summary>
    int BatchSize { get; }
}
