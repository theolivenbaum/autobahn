using Autobahn.Feeds;

namespace Autobahn.Internal.Domain.Feeds;

/// <summary>
/// Items pulled lazily from a sequence, for a dataset too big to hold in memory.
/// </summary>
/// <remarks>
/// An enumerator is not thread-safe and cannot be made so cheaply, so unlike the other feeds
/// this one takes a lock per item. That is the price of not loading the file: a feed reading
/// a million-row CSV row by row is bounded by the disk, not by the lock, and one that fits in
/// memory should use <see cref="CircularFeed{T}"/> instead.
///
/// Restart reopens the source through the factory rather than rewinding the enumerator, which
/// is the only honest way to do it - the sequence may be a file handle, and rewinding it is
/// not something the interface can promise.
/// </remarks>
internal sealed class StreamingFeed<T> : IFeed<T>
{
    private readonly Func<IEnumerable<T>> _openSource;
    private readonly FeedExhaustion _onExhausted;
    private readonly Lock _sync = new();

    private IEnumerator<T> _enumerator;
    private int _served;
    private bool _finished;

    public StreamingFeed(string feedName, Func<IEnumerable<T>> openSource, FeedExhaustion onExhausted)
    {
        FeedName = feedName;
        _openSource = openSource;
        _onExhausted = onExhausted;
        _enumerator = openSource().GetEnumerator();
    }

    public string FeedName { get; }

    /// <summary>How many items this feed has handed out over the run.</summary>
    public int Served
    {
        get { lock (_sync) return _served; }
    }

    public T Next()
    {
        lock (_sync)
        {
            if (!_finished && _enumerator.MoveNext())
            {
                _served++;
                return _enumerator.Current;
            }

            if (_onExhausted != FeedExhaustion.Restart)
            {
                _finished = true;
                throw new FeedExhaustedException(FeedName, _served);
            }

            // Reopen and try once more. A source that comes back empty on reopen would loop
            // forever if we kept trying, so a single retry is all it gets.
            _enumerator.Dispose();
            _enumerator = _openSource().GetEnumerator();

            if (!_enumerator.MoveNext())
            {
                _finished = true;
                throw new FeedExhaustedException(FeedName, _served);
            }

            _served++;
            return _enumerator.Current;
        }
    }
}
