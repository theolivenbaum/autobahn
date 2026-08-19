namespace Autobahn;

/// <summary>
/// Helpers for splitting work across the copies of a scenario, so each copy takes its own
/// share of a dataset instead of every copy fighting over the same rows.
/// </summary>
public static class ScenarioContextExtensions
{
    /// <summary>
    /// True when the item at <paramref name="index"/> belongs to this scenario copy.
    /// Copy 7 of 100 owns indexes 7, 107, 207 and so on.
    /// </summary>
    public static bool OwnsIndex(this IScenarioContext context, int index)
    {
        var copyCount = context.ScenarioInfo.CopyCount;
        if (copyCount <= 1) return true;

        return index % copyCount == context.ScenarioInfo.ThreadNumber % copyCount;
    }

    /// <summary>The slice of <paramref name="items"/> this scenario copy is responsible for.</summary>
    public static IEnumerable<T> Partition<T>(this IScenarioContext context, IReadOnlyList<T> items)
    {
        var copyCount = context.ScenarioInfo.CopyCount;
        if (copyCount <= 1) return items;

        var offset = context.ScenarioInfo.ThreadNumber % copyCount;
        return Slice(items, offset, copyCount);

        static IEnumerable<T> Slice(IReadOnlyList<T> source, int offset, int step)
        {
            for (var i = offset; i < source.Count; i += step) yield return source[i];
        }
    }

    /// <summary>
    /// The item this copy should use for the given iteration, walking only the copy's own
    /// share of the list. Returns null when the list is empty.
    /// </summary>
    public static T? ItemForIteration<T>(this IScenarioContext context, IReadOnlyList<T> items) where T : class
    {
        if (items.Count == 0) return null;

        var copyCount = Math.Max(1, context.ScenarioInfo.CopyCount);
        var offset = context.ScenarioInfo.ThreadNumber % copyCount;

        // Walk this copy's own stride, wrapping when it runs off the end.
        var ownCount = (items.Count - offset + copyCount - 1) / copyCount;
        if (ownCount <= 0) return items[offset % items.Count];

        var position = offset + copyCount * (Math.Max(0, context.InvocationNumber - 1) % ownCount);
        return items[position % items.Count];
    }
}
