namespace Autobahn.Internal.Domain.Feeds;

/// <summary>The checks every in-memory feed makes before it will hand anything out.</summary>
internal static class FeedValidation
{
    /// <summary>
    /// An empty feed cannot answer <c>Next()</c> at all, so it fails at construction rather
    /// than on the first iteration - which would be inside user code, under load, in a stack
    /// trace that says nothing about the config file the data came from.
    /// </summary>
    public static void RequireItems<T>(string feedName, IReadOnlyList<T> items)
    {
        if (string.IsNullOrWhiteSpace(feedName))
            throw new AutobahnException("A feed needs a name.");

        if (items.Count == 0)
            throw new AutobahnException($"Feed '{feedName}' was given no items. A feed needs at least one.");
    }
}
