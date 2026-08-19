using System.Buffers.Binary;
using System.Diagnostics;

namespace Autobahn.Mqtt;

/// <summary>
/// Writes the moment a message was published into the message, and reads it back.
/// </summary>
/// <remarks>
/// This is what makes end-to-end delivery latency measurable at all. A publisher scenario and
/// a consumer scenario are independent - neither knows what the other did - so the only place
/// the send time can live is in the message.
///
/// The stamp goes in the payload rather than in a header because MQTT 3.1.1 has no user
/// properties, and a helper that only worked against MQTT 5 brokers would be a helper that
/// worked against half of them. The AMQP helper does use a header, because AMQP has one.
///
/// The clock is <see cref="Stopwatch.GetTimestamp"/>, which is monotonic and has no meaning
/// outside this process. That is the right trade here: publisher and consumer are two scenarios
/// in one load generator, so the readings are comparable, and unlike a wall clock this one
/// cannot step backwards mid-run and report a negative latency. A publisher in a *different*
/// process is out of scope, and says so rather than reporting nonsense - the magic number is
/// what a foreign message fails to carry.
/// </remarks>
public static class MqttDelivery
{
    /// <summary>
    /// Four bytes that say "the next eight are a timestamp".
    /// </summary>
    /// <remarks>
    /// Not a guarantee, a filter: it is here so that a message published by something other
    /// than this test is reported as unstamped rather than read as having been delivered some
    /// absurd number of milliseconds ago.
    /// </remarks>
    private static readonly byte[] Magic = "AbTs"u8.ToArray();

    /// <summary>How many bytes a stamp adds to a payload.</summary>
    public const int Length = 12;

    /// <summary>Returns the body with a stamp in front of it.</summary>
    public static byte[] Stamp(byte[] body)
    {
        var stamped = new byte[Length + body.Length];

        Magic.CopyTo(stamped, 0);
        BinaryPrimitives.WriteInt64LittleEndian(stamped.AsSpan(Magic.Length), Stopwatch.GetTimestamp());
        body.CopyTo(stamped, Length);

        return stamped;
    }

    /// <summary>
    /// Reads a stamp back, giving how long the message took to arrive and the body without it.
    /// </summary>
    /// <returns>False for anything this test did not stamp.</returns>
    public static bool TryRead(byte[] payload, out TimeSpan delay, out byte[] body)
    {
        delay = TimeSpan.Zero;
        body = payload;

        if (payload.Length < Length) return false;
        if (!payload.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return false;

        var published = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(Magic.Length));

        // A stamp from the future is a stamp from another process that happened to start with
        // the same four bytes. Reporting it as a negative latency would be worse than refusing.
        if (published > Stopwatch.GetTimestamp()) return false;

        delay = Stopwatch.GetElapsedTime(published);
        body = payload[Length..];

        return true;
    }
}
