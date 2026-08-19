using System.Buffers.Binary;
using System.Diagnostics;
using RabbitMQ.Client;

namespace Autobahn.Amqp;

/// <summary>
/// Writes the moment a message was published into one of its headers, and reads it back.
/// </summary>
/// <remarks>
/// This is what makes end-to-end delivery latency measurable at all. A publisher scenario and
/// a consumer scenario are independent - neither knows what the other did - so the only place
/// the send time can live is in the message.
///
/// A header rather than a payload prefix, which is where the MQTT helper has to put it: AMQP
/// has headers, and stamping the body would mean every consumer had to know to skip twelve
/// bytes before parsing its own format. A header leaves the body exactly as the test wrote it.
///
/// The clock is <see cref="Stopwatch.GetTimestamp"/>, which is monotonic and has no meaning
/// outside this process. That is the right trade here: publisher and consumer are two
/// scenarios in one load generator, so the readings are comparable, and unlike a wall clock
/// this one cannot step backwards mid-run and report a negative latency. A publisher in a
/// *different* process is out of scope, and a message from one is reported as unstamped rather
/// than as an absurd number.
/// </remarks>
public static class AmqpDelivery
{
    /// <summary>The header the stamp travels in.</summary>
    public const string Header = "x-autobahn-published";

    /// <summary>Puts a stamp on a set of properties, creating the header table if it has none.</summary>
    public static BasicProperties Stamp(BasicProperties properties)
    {
        properties.Headers ??= new Dictionary<string, object?>();

        var stamp = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(stamp, Stopwatch.GetTimestamp());

        properties.Headers[Header] = stamp;

        return properties;
    }

    /// <summary>A fresh set of properties carrying nothing but a stamp.</summary>
    public static BasicProperties Stamped() => Stamp(new BasicProperties());

    /// <summary>
    /// Reads a stamp back, giving how long the message took to arrive.
    /// </summary>
    /// <returns>False for anything this test did not stamp.</returns>
    public static bool TryRead(IReadOnlyBasicProperties properties, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;

        if (properties.Headers is not { } headers) return false;
        if (!headers.TryGetValue(Header, out var value) || value is not byte[] stamp) return false;
        if (stamp.Length != sizeof(long)) return false;

        var published = BinaryPrimitives.ReadInt64LittleEndian(stamp);

        // A stamp from the future came from another process, whose ticks mean something else.
        // Reporting it as a negative latency would be worse than refusing it.
        if (published > Stopwatch.GetTimestamp()) return false;

        delay = Stopwatch.GetElapsedTime(published);

        return true;
    }
}
