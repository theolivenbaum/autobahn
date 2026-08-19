namespace Autobahn.Internal.Domain;

/// <summary>One completed step or iteration, on its way to the stats actor.</summary>
/// <remarks>
/// A struct: this is the hot path, and one of these is produced for every step of every
/// iteration. It carries a reference to the user's response rather than copying it.
/// </remarks>
internal readonly record struct Measurement(
    string Name,
    IResponse ClientResponse,
    TimeSpan CurrentTimeBucket,
    TimeSpan Latency);
