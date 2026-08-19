namespace Autobahn.Internal.Domain;

/// <summary>A request from inside user code to end something early.</summary>
internal abstract record StopCommand
{
    private StopCommand() { }

    public sealed record StopScenario(string ScenarioName, string Reason) : StopCommand;

    public sealed record StopTest(string Reason) : StopCommand;
}
