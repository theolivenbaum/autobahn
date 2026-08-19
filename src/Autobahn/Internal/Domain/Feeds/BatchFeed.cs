using Autobahn.Feeds;

namespace Autobahn.Internal.Domain.Feeds;

/// <summary>
/// A group of items per call, taken in order.
/// </summary>
/// <remarks>
/// The batches are cut once, at construction, rather than sliced per call: an iteration that
/// asks for a batch gets an array it can hold without copying, and the hot path is the same
/// interlocked increment the circular feed uses. The last batch is short when the item count
/// is not a multiple of the size - dropping it would quietly lose data, and padding it would
/// quietly invent some.
/// </remarks>
internal sealed class BatchFeed<T> : IBatchFeed<T>
{
    private readonly IReadOnlyList<T>[] _batches;
    private readonly FeedExhaustion _onExhausted;
    private readonly int _itemCount;

    private long _cursor = -1;

    public BatchFeed(string feedName, IReadOnlyList<T> items, int batchSize, FeedExhaustion onExhausted)
    {
        FeedValidation.RequireItems(feedName, items);

        if (batchSize < 1)
            throw new AutobahnException($"Feed '{feedName}' was given a batch size of {batchSize}. It has to be at least 1.");

        FeedName = feedName;
        BatchSize = batchSize;
        _onExhausted = onExhausted;
        _itemCount = items.Count;

        _batches = Cut(items, batchSize);
    }

    public string FeedName { get; }
    public int BatchSize { get; }

    /// <summary>How many batches the items were cut into. The last one may be short.</summary>
    public int BatchCount => _batches.Length;

    public IReadOnlyList<T> Next()
    {
        var index = Interlocked.Increment(ref _cursor);

        if (index >= _batches.Length && _onExhausted != FeedExhaustion.Restart)
            throw new FeedExhaustedException(FeedName, _itemCount);

        return _batches[(int)(index % _batches.Length)];
    }

    private static IReadOnlyList<T>[] Cut(IReadOnlyList<T> items, int batchSize)
    {
        var count = (items.Count + batchSize - 1) / batchSize;
        var batches = new IReadOnlyList<T>[count];

        for (var i = 0; i < count; i++)
        {
            var start = i * batchSize;
            var length = Math.Min(batchSize, items.Count - start);
            var batch = new T[length];

            for (var j = 0; j < length; j++) batch[j] = items[start + j];

            batches[i] = batch;
        }

        return batches;
    }
}
