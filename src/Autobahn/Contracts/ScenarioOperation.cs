namespace Autobahn;

/// <summary>Which phase of the session a scenario copy is executing in.</summary>
public enum ScenarioOperation
{
    Init = 0,
    Clean = 1,
    WarmUp = 2,
    Bombing = 3
}
