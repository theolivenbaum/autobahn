using System.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Autobahn.Internal.Domain.Metrics;

/// <summary>
/// Bytes the process sent and received at the socket level, read off the runtime's own
/// <c>System.Net.Sockets</c> event source.
/// </summary>
/// <remarks>
/// There is no API that just asks for this, and counting bytes in the scenario would only
/// see what the scenario handed to a client - not headers, not TLS, not retries. The event
/// source is what the runtime already maintains, so reading it costs nothing on the hot path.
///
/// It is also entirely best-effort: the source may not exist, may be renamed, or may be
/// disabled by the host. Every failure here degrades to "no socket metrics" rather than to a
/// failed run, which is why <see cref="TryStart"/> returns null instead of throwing.
/// </remarks>
internal sealed class SocketTraffic : EventListener
{
    private const string SourceName = "System.Net.Sockets";

    // The listener's base constructor can raise OnEventSourceCreated before this type's own
    // field initialisers have run, so nothing here may depend on construction order.
    private long _bytesSent;
    private long _bytesReceived;

    private ILogger? _logger;
    private EventSource? _source;

    private SocketTraffic() { }

    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    public static SocketTraffic? TryStart(ILogger logger)
    {
        try
        {
            var listener = new SocketTraffic { _logger = logger };
            listener.EnableIfFound();
            return listener;
        }
        catch (Exception ex)
        {
            logger.ZLogDebug($"Socket traffic metrics are unavailable: {ex.Message}");
            return null;
        }
    }

    private void EnableIfFound()
    {
        if (_source is null) return;

        EnableEvents(
            _source,
            EventLevel.Informational,
            EventKeywords.All,
            new Dictionary<string, string?>
            {
                // The runtime only publishes counters when a listener asks for an interval.
                ["EventCounterIntervalSec"] = Constants.SocketCounterIntervalSec.ToString()
            });
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name != SourceName) return;

        _source = eventSource;

        // A source that already existed when the listener was constructed arrives here before
        // _logger is set, so enabling is deferred to TryStart in that case.
        if (_logger is not null) EnableIfFound();
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName != "EventCounters" || eventData.Payload is null) return;

        foreach (var item in eventData.Payload)
        {
            if (item is not IDictionary<string, object> counter) continue;
            if (!counter.TryGetValue("Name", out var name)) continue;

            // Incrementing counters report the increment for the window, not a running total.
            if (!counter.TryGetValue("Increment", out var increment)) continue;
            if (increment is not double delta || delta <= 0) continue;

            switch (name as string)
            {
                case "bytes-sent":
                    Interlocked.Add(ref _bytesSent, (long)delta);
                    break;

                case "bytes-received":
                    Interlocked.Add(ref _bytesReceived, (long)delta);
                    break;
            }
        }
    }
}
