using Autobahn.Feeds;

namespace Autobahn.Internal.Domain.Feeds;

/// <summary>
/// A uniformly random item each call.
/// </summary>
/// <remarks>
/// Unseeded, this uses <see cref="System.Random.Shared"/>, which is per-thread and needs no
/// lock. A seeded feed exists so a run can be reproduced, and that one does need a lock -
/// a single <see cref="System.Random"/> is not thread-safe, and a torn one silently returns
/// zeros. Reproducibility is worth a lock; the default path does not pay for it.
/// </remarks>
internal sealed class RandomFeed<T> : IFeed<T>
{
    private readonly IReadOnlyList<T> _items;
    private readonly Random? _seeded;
    private readonly Lock _sync = new();

    public RandomFeed(string feedName, IReadOnlyList<T> items, int? seed)
    {
        FeedValidation.RequireItems(feedName, items);

        FeedName = feedName;
        _items = items;
        _seeded = seed is { } value ? new Random(value) : null;
    }

    public string FeedName { get; }

    public T Next()
    {
        if (_seeded is null) return _items[System.Random.Shared.Next(_items.Count)];

        lock (_sync) return _items[_seeded.Next(_items.Count)];
    }
}
