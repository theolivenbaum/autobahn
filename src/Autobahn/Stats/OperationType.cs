namespace Autobahn.Stats;

/// <summary>Where a run currently is in its lifecycle.</summary>
public enum OperationType
{
    None = 0,
    Init = 1,
    WarmUp = 2,
    Bombing = 3,
    Stop = 4,
    Complete = 5,
    Error = 6
}
