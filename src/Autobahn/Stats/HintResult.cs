namespace Autobahn.Stats;

/// <summary>A piece of post-run advice about the test itself.</summary>
public sealed record HintResult
{
    public required string SourceName { get; init; }
    public required HintSourceType SourceType { get; init; }
    public required string Hint { get; init; }
}
