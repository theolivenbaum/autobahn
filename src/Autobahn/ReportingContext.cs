using Autobahn.Stats;

namespace Autobahn;

/// <summary>Where reports go and how often live statistics are produced.</summary>
public sealed record ReportingContext
{
    public string? FolderName { get; init; }
    public string? FileName { get; init; }
    public required IReadOnlyList<ReportFormat> Formats { get; init; }
    public required TimeSpan ReportingInterval { get; init; }
}
