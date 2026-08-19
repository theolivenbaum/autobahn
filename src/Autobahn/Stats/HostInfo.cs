namespace Autobahn.Stats;

/// <summary>
/// The machine the run executed on, plus the operation it is currently performing.
/// (This is the fork point's NodeInfo with its cluster NodeType removed - what is left
/// is genuinely about the host, so the type kept its data and lost its name.)
/// </summary>
public sealed record HostInfo
{
    public required string MachineName { get; init; }
    public required OperationType CurrentOperation { get; init; }
    public required string OS { get; init; }
    public required string DotNetVersion { get; init; }
    public required string Processor { get; init; }
    public required int CoresCount { get; init; }
    public required string AutobahnVersion { get; init; }

    public static HostInfo Empty { get; } = new()
    {
        MachineName = "",
        CurrentOperation = OperationType.None,
        OS = "",
        DotNetVersion = "",
        Processor = "",
        CoresCount = 0,
        AutobahnVersion = ""
    };
}
