namespace Autobahn;

/// <summary>
/// The process exit codes a run produces, so a CI job and the caller's own <c>Main</c> can
/// agree on what they mean without repeating the numbers.
/// </summary>
/// <remarks>
/// Autobahn sets <see cref="ThresholdFailed"/> itself when a threshold fails, unless the run
/// opted out with <c>WithoutThresholdExitCode()</c>. Nothing here ends the process - a library
/// does not get to decide that - so a caller returning its own code from <c>Main</c> overrides
/// whatever Autobahn set.
/// </remarks>
public static class AutobahnExitCode
{
    /// <summary>The run finished and every threshold passed.</summary>
    public const int Ok = 0;

    /// <summary>The run could not start, or ended in an error.</summary>
    public const int Error = 1;

    /// <summary>The run finished and at least one threshold failed.</summary>
    public const int ThresholdFailed = 2;
}
