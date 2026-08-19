namespace Autobahn.Stats;

/// <summary>A report Autobahn wrote to disk.</summary>
public sealed record ReportFile
{
    public required string FilePath { get; init; }
    public required ReportFormat ReportFormat { get; init; }
    public required string ReportContent { get; init; }
}
