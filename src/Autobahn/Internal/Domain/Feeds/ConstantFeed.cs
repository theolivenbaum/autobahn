using Autobahn.Feeds;

namespace Autobahn.Internal.Domain.Feeds;

/// <summary>One item, chosen once, handed to every iteration.</summary>
internal sealed class ConstantFeed<T> : IFeed<T>
{
    private readonly T _item;

    public ConstantFeed(string feedName, IReadOnlyList<T> items)
    {
        FeedValidation.RequireItems(feedName, items);

        FeedName = feedName;
        _item = items[System.Random.Shared.Next(items.Count)];
    }

    public string FeedName { get; }

    public T Next() => _item;
}
