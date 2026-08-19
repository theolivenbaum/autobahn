namespace Autobahn.Configuration;

/// <summary>The whole JSON config document (autobahn-config.json).</summary>
public sealed record AutobahnConfig
{
    public string? TestSuite { get; init; }
    public string? TestName { get; init; }
    public IReadOnlyList<string>? TargetScenarios { get; init; }
    public GlobalSettings? GlobalSettings { get; init; }

    /// <summary>True when the document carried at least one setting Autobahn understands.</summary>
    internal bool HasAnySetting =>
        GlobalSettings is not null || TargetScenarios is not null || TestName is not null || TestSuite is not null;
}
