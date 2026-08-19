namespace Autobahn.Stats;

/// <summary>Identifies the run: which suite, which test, which session.</summary>
public sealed record TestInfo
{
    public required string SessionId { get; init; }
    public required string TestSuite { get; init; }
    public required string TestName { get; init; }

    public static TestInfo Empty { get; } = new() { SessionId = "", TestSuite = "", TestName = "" };
}
