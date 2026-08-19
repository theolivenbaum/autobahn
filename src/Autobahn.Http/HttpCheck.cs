namespace Autobahn.Http;

/// <summary>
/// A rule the answer has to satisfy for the request to count as a success.
/// </summary>
/// <remarks>
/// A check that needs the body forces Autobahn to read it, which most requests would rather
/// avoid - so a check says whether it needs one, and the body is only read when something
/// asked for it.
/// </remarks>
public sealed record HttpCheck
{
    private HttpCheck() { }

    /// <summary>What the failure message says was expected.</summary>
    public required string Description { get; init; }

    /// <summary>True when the check needs the response body, and the body must be read for it.</summary>
    public required bool NeedsBody { get; init; }

    /// <summary>
    /// The rule itself. The body argument is empty when <see cref="NeedsBody"/> is false, which
    /// is why the two-argument factory is the only way to get one that reads it.
    /// </summary>
    public required Func<HttpResponseMessage, string, bool> Predicate { get; init; }

    /// <summary>A check over the response's status and headers, which needs no body.</summary>
    public static HttpCheck Create(string description, Func<HttpResponseMessage, bool> predicate) => new()
    {
        Description = description,
        NeedsBody = false,
        Predicate = (response, _) => predicate(response)
    };

    /// <summary>A check over the response and its body, which forces the body to be read.</summary>
    public static HttpCheck Create(string description, Func<HttpResponseMessage, string, bool> predicate) => new()
    {
        Description = description,
        NeedsBody = true,
        Predicate = predicate
    };
}
