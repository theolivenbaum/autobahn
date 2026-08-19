namespace Autobahn;

/// <summary>Picks which piece of work an iteration should do.</summary>
public interface IWorkloadDistribution<out T>
{
    /// <summary>The next item, drawn according to this distribution.</summary>
    T Next();
}

/// <summary>
/// Ready-made ways to choose <em>which</em> work an iteration does.
/// </summary>
/// <remarks>
/// Uniform-random is the wrong default for most systems under test. A cache, a CDN or a
/// content store sees a hot minority of keys and a long tail; testing it with a uniform
/// draw measures a cache that never hits. Zipfian is the realistic default there, and
/// multinomial is how you say "80% reads, 15% writes, 5% deletes" without hand-computing
/// per-operation rates.
/// </remarks>
public static class Distribution
{
    /// <summary>Every item equally likely.</summary>
    public static IWorkloadDistribution<T> Uniform<T>(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) throw new ArgumentException("A distribution needs at least one item.", nameof(items));

        return new UniformDistribution<T>(items);
    }

    /// <summary>
    /// A hot minority and a long tail: item <c>i</c> is drawn with probability proportional
    /// to <c>1 / (i + 1)^skew</c>, so the list's order is its popularity order.
    /// </summary>
    /// <param name="items">Most popular first.</param>
    /// <param name="skew">
    /// How concentrated the head is. 1.0 is the classic Zipf law; higher concentrates
    /// further on the first few items, and values near zero approach uniform.
    /// </param>
    public static IWorkloadDistribution<T> Zipfian<T>(IReadOnlyList<T> items, double skew = 1.0)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) throw new ArgumentException("A distribution needs at least one item.", nameof(items));
        if (skew < 0.0) throw new ArgumentOutOfRangeException(nameof(skew), skew, "Skew cannot be negative.");

        return new WeightedDistribution<T>(items, BuildZipfWeights(items.Count, skew));
    }

    /// <summary>An explicit weighted choice between named alternatives.</summary>
    /// <param name="choices">Weights need not sum to anything in particular; they are normalised.</param>
    public static IWorkloadDistribution<T> Multinomial<T>(params (T Item, double Weight)[] choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Length == 0) throw new ArgumentException("A distribution needs at least one choice.", nameof(choices));

        if (choices.Any(x => x.Weight < 0.0))
            throw new ArgumentException("A choice cannot have a negative weight.", nameof(choices));

        if (choices.Sum(x => x.Weight) <= 0.0)
            throw new ArgumentException("At least one choice needs a weight above zero.", nameof(choices));

        return new WeightedDistribution<T>(
            choices.Select(x => x.Item).ToArray(),
            choices.Select(x => x.Weight).ToArray());
    }

    private static double[] BuildZipfWeights(int count, double skew)
    {
        var weights = new double[count];
        for (var i = 0; i < count; i++) weights[i] = 1.0 / Math.Pow(i + 1, skew);
        return weights;
    }

    private sealed class UniformDistribution<T>(IReadOnlyList<T> items) : IWorkloadDistribution<T>
    {
        public T Next() => items[Random.Shared.Next(items.Count)];
    }

    /// <summary>
    /// Draws from a precomputed cumulative table by binary search, so a draw is O(log n)
    /// and allocates nothing however large the item list is.
    /// </summary>
    private sealed class WeightedDistribution<T> : IWorkloadDistribution<T>
    {
        private readonly IReadOnlyList<T> _items;
        private readonly double[] _cumulative;

        public WeightedDistribution(IReadOnlyList<T> items, IReadOnlyList<double> weights)
        {
            _items = items;
            _cumulative = new double[weights.Count];

            var running = 0.0;

            for (var i = 0; i < weights.Count; i++)
            {
                running += weights[i];
                _cumulative[i] = running;
            }

            var total = _cumulative[^1];
            for (var i = 0; i < _cumulative.Length; i++) _cumulative[i] /= total;

            // Guard against the last bucket ending a hair below 1.0 after the division.
            _cumulative[^1] = 1.0;
        }

        public T Next()
        {
            var draw = Random.Shared.NextDouble();
            var index = Array.BinarySearch(_cumulative, draw);

            if (index < 0) index = ~index;
            if (index >= _items.Count) index = _items.Count - 1;

            return _items[index];
        }
    }
}
