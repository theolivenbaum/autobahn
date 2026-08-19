using System.Runtime.CompilerServices;

namespace Autobahn;

/// <summary>Unit conversions Autobahn uses when it reports numbers, exposed so tests can use them too.</summary>
public static class Converter
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FromMicroSecToMs(double microSec) => microSec / 1000.0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FromMsToMicroSec(double ms) => (int)(ms * 1000.0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FromBytesToKb(long bytes) => Math.Round(bytes / 1024.0, 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static decimal FromBytesToMb(long bytes) => Math.Round(bytes / 1024.0M / 1024.0M, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Round(double value, int digits) => Math.Round(value, digits);

    /// <summary>Drops sub-second precision, so a duration reads as hh:mm:ss in a report.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TimeSpan RoundDuration(TimeSpan duration) =>
        new(duration.Days, duration.Hours, duration.Minutes, duration.Seconds);
}
