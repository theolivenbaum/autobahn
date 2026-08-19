using System.Collections.Concurrent;
using System.Threading.Channels;
using Autobahn.Ui.Contracts;

namespace Autobahn.Cli.Ui;

/// <summary>
/// Everything the UI reads, in one place, between the run producing it and a browser asking
/// for it.
/// </summary>
/// <remarks>
/// The load test writes here once per reporting interval whether or not anyone is watching,
/// and readers take what they find. That separation is the whole design constraint from
/// TODO.md section 8: no client, twenty clients, one on a slow link, the tab closed
/// mid-run - the run's timing, results and exit code are identical, because nothing a client
/// does can reach back into the engine.
/// </remarks>
internal sealed class RunFeed(UiOptions options)
{
    private readonly LiveFrame?[] _history = new LiveFrame[options.HistoryCapacity];
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly Lock _sync = new();

    private long _sequence;
    private long _oldestSequence = 1;

    private RunDescriptor _run = new();
    private LiveFrame? _latest;
    private ReportDescriptor[] _reports = [];

    /// <summary>Invoked when a client asks the run to stop. Null while nothing can stop it.</summary>
    public Action<bool>? OnStopRequested { get; set; }

    /// <summary>
    /// Where this run's reports are written, once the engine has resolved it.
    /// </summary>
    /// <remarks>
    /// The comparison screen reads previous runs out of the folder beside this one, so the
    /// history exists only once this is known - which is after the configuration has been
    /// merged, not when the server started.
    /// </remarks>
    public string ReportFolder { get; set; } = "";

    /// <summary>This run's session id, so the history can tell which entry is the current run.</summary>
    public string SessionId { get; set; } = "";

    public RunDescriptor Run
    {
        get { lock (_sync) return _run; }
        set { lock (_sync) _run = value; }
    }

    public void SetReports(ReportDescriptor[] reports)
    {
        lock (_sync) _reports = reports;
    }

    /// <summary>Numbers the frame, keeps it, and hands it to every subscriber.</summary>
    public void Publish(LiveFrame frame)
    {
        LiveFrame numbered;

        lock (_sync)
        {
            // The wire carries sequence numbers as doubles, because the browser at the far end
            // has no other kind of number; the ring arithmetic stays on the long.
            numbered = frame with { Sequence = ++_sequence };

            var slot = (int)((_sequence - 1) % _history.Length);
            _history[slot] = numbered;

            if (_sequence > _history.Length) _oldestSequence = _sequence - _history.Length + 1;

            _latest = numbered;
        }

        foreach (var subscriber in _subscribers.Values) subscriber.Offer(numbered);
    }

    public RunSnapshot Snapshot()
    {
        lock (_sync)
        {
            var (frames, downsampled) = ReadHistory(_oldestSequence);

            return new RunSnapshot
            {
                Run = _run,
                Latest = _latest,
                History = frames,
                HistoryDownsampled = downsampled,
                Reports = _reports
            };
        }
    }

    public HistoryResponse History(long fromSequence)
    {
        lock (_sync)
        {
            var (frames, downsampled) = ReadHistory(Math.Max(fromSequence, _oldestSequence));

            return new HistoryResponse
            {
                Frames = frames,
                OldestSequence = _oldestSequence,
                Downsampled = downsampled
            };
        }
    }

    /// <summary>
    /// The frames from <paramref name="from"/> onwards, thinned when there are too many to
    /// hand a browser.
    /// </summary>
    /// <remarks>
    /// Thinned here rather than in the browser: a page opened into hour six of a run should
    /// not be asked to draw four thousand points a series, and the host is the only end that
    /// can decide that before the bytes are on the wire. The most recent frames are never
    /// thinned - the near past is what somebody is actually looking at.
    /// </remarks>
    private (LiveFrame[] Frames, bool Downsampled) ReadHistory(long from)
    {
        const int maxPoints = 1_000;
        const int keepRecent = 200;

        var available = new List<LiveFrame>();

        for (var sequence = from; sequence <= _sequence; sequence++)
        {
            var frame = _history[(int)((sequence - 1) % _history.Length)];

            // A slot the ring has already overwritten; the caller asked for too far back.
            if (frame is null || frame.Sequence != sequence) continue;

            available.Add(frame);
        }

        if (available.Count <= maxPoints) return (available.ToArray(), false);

        var recent = available.Skip(available.Count - keepRecent).ToArray();
        var older = available.Take(available.Count - keepRecent).ToArray();

        var step = (int)Math.Ceiling(older.Length / (double)(maxPoints - keepRecent));
        var thinned = older.Where((_, i) => i % step == 0);

        return ([.. thinned, .. recent], true);
    }

    /// <summary>Registers a client. Disposing the subscription unregisters it.</summary>
    public Subscriber Subscribe()
    {
        var subscriber = new Subscriber(options.ClientQueueCapacity, id => _subscribers.TryRemove(id, out _));
        _subscribers[subscriber.Id] = subscriber;

        return subscriber;
    }

    public int SubscriberCount => _subscribers.Count;

    /// <summary>
    /// One connected client's queue.
    /// </summary>
    /// <remarks>
    /// Bounded and drop-oldest. A client on a slow link falls behind, loses the frames it was
    /// too slow for, and notices from the sequence numbers - which is exactly what should
    /// happen. The alternative is back-pressure, and back-pressure from a browser onto a load
    /// generator would make the watcher part of the measurement.
    /// </remarks>
    internal sealed class Subscriber(int capacity, Action<Guid> onDispose) : IDisposable
    {
        private readonly Channel<LiveFrame> _channel = Channel.CreateBounded<LiveFrame>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

        public Guid Id { get; } = Guid.NewGuid();

        public void Offer(LiveFrame frame) => _channel.Writer.TryWrite(frame);

        public IAsyncEnumerable<LiveFrame> ReadAllAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        public void Dispose()
        {
            _channel.Writer.TryComplete();
            onDispose(Id);
        }
    }
}
