namespace Autobahn.Stats;

/// <summary>What a completed run hands back: the final numbers, the history behind them, and any hints.</summary>
public sealed record SessionResult
{
    public required SessionStats FinalStats { get; init; }
    public required TimeLineHistoryRecord[] TimeLineHistory { get; init; }
    public required HintResult[] Hints { get; init; }

    public static SessionResult Empty { get; } = new()
    {
        FinalStats = SessionStats.Empty,
        TimeLineHistory = [],
        Hints = []
    };
}
