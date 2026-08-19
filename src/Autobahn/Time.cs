namespace Autobahn;

/// <summary>Short constructors for the durations a load plan is written in.</summary>
public static class Time
{
    public static TimeSpan Milliseconds(double value) => TimeSpan.FromMilliseconds(value);
    public static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);
    public static TimeSpan Minutes(double value) => TimeSpan.FromMinutes(value);
    public static TimeSpan Hours(double value) => TimeSpan.FromHours(value);
}
