namespace Autobahn.Stats;

/// <summary>What produced a hint.</summary>
public enum HintSourceType
{
    Scenario = 0,
    WorkerPlugin = 1,

    /// <summary>The load generator itself, rather than anything the test declared.</summary>
    LoadGenerator = 2
}
