using Autobahn.Feeds;

namespace Autobahn.Internal.Domain.Feeds;

/// <summary>
/// Items in order, one per call, wrapping at the end.
/// </summary>
/// <remarks>
/// The cursor is a single interlocked increment and the item array is never written after
/// construction, so every copy of a scenario can pull from one of these at once without a
/// lock. The cursor is allowed to run past the item count and is folded back with a modulo
/// on read, which is what keeps the increment lock-free.
/// </remarks>
internal sealed class CircularFeed<T> : IFeed<T>
{
    private readonly IReadOnlyList<T> _items;
    private readonly FeedExhaustion _onExhausted;

    private long _cursor = -1;

    public CircularFeed(string feedName, IReadOnlyList<T> items, FeedExhaustion onExhausted)
    {
        FeedValidation.RequireItems(feedName, items);

        FeedName = feedName;
        _items = items;
        _onExhausted = onExhausted;
    }

    public string FeedName { get; }

    public T Next()
    {
        var index = Interlocked.Increment(ref _cursor);

        if (index >= _items.Count && _onExhausted != FeedExhaustion.Restart)
            throw new FeedExhaustedException(FeedName, _items.Count);

        return _items[(int)(index % _items.Count)];
    }
}
