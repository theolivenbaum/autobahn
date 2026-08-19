using Autobahn.Internal.Domain.Feeds;

namespace Autobahn.Feeds;

/// <summary>
/// The feeds that come with Autobahn: how an iteration picks the data it works on.
/// </summary>
/// <remarks>
/// Three orders, and a source. The order decides which item an iteration gets; the source
/// decides where the items come from. They compose - any order over any source - so adding
/// a source does not mean adding three feeds.
/// </remarks>
public static class Feed
{
    /// <summary>
    /// Hands out items in order, one per call, looping when it reaches the end.
    /// </summary>
    /// <remarks>
    /// The one to reach for by default: every item is used before any is reused, which is
    /// what makes a run reproducible and what stops a cache test measuring one hot key.
    /// </remarks>
    public static IFeed<T> Circular<T>(
        string feedName, IReadOnlyList<T> items, FeedExhaustion onExhausted = FeedExhaustion.Restart) =>
        new CircularFeed<T>(feedName, items, onExhausted);

    /// <summary>Hands out the same item to every iteration, chosen at random once.</summary>
    public static IFeed<T> Constant<T>(string feedName, IReadOnlyList<T> items) =>
        new ConstantFeed<T>(feedName, items);

    /// <summary>
    /// Hands out a uniformly random item each call. Never exhausts, and gives no guarantee
    /// that every item is ever used.
    /// </summary>
    public static IFeed<T> Random<T>(string feedName, IReadOnlyList<T> items, int? seed = null) =>
        new RandomFeed<T>(feedName, items, seed);

    /// <summary>
    /// Hands out a whole group of items per call, which is how a scenario models a batched
    /// API without doing the grouping itself.
    /// </summary>
    public static IBatchFeed<T> Batch<T>(
        string feedName,
        IReadOnlyList<T> items,
        int batchSize,
        FeedExhaustion onExhausted = FeedExhaustion.Restart) =>
        new BatchFeed<T>(feedName, items, batchSize, onExhausted);

    /// <summary>
    /// Reads items lazily from a sequence instead of loading them all first, for a dataset
    /// too big to hold in memory.
    /// </summary>
    /// <remarks>
    /// A streaming feed is finite by nature and cannot restart, because the sequence behind
    /// it has already been consumed - so <see cref="FeedExhaustion.Restart"/> is not one of
    /// its options and asking for it is an error. Pass a factory rather than a sequence when
    /// you want restart: reading the file again is the only honest way to do it.
    /// </remarks>
    public static IFeed<T> Streaming<T>(
        string feedName,
        Func<IEnumerable<T>> openSource,
        FeedExhaustion onExhausted = FeedExhaustion.Restart) =>
        new StreamingFeed<T>(feedName, openSource, onExhausted);
}
