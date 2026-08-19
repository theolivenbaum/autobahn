namespace Autobahn.Internal;

/// <summary>
/// Thrown out of a step when the iteration should restart rather than continue.
/// Caught by the scenario's own measurement wrapper; it never escapes to user code.
/// </summary>
internal sealed class RestartScenarioIterationException : Exception
{
    public RestartScenarioIterationException() : base("restart scenario iteration") { }
}
