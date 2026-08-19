namespace Autobahn.Configuration;

/// <summary>Where an effective setting's value came from.</summary>
/// <remarks>
/// Listed weakest to strongest: a later source overrides an earlier one. That order is the
/// documented precedence, and <see cref="EffectiveSetting"/> reports which link in it won.
/// </remarks>
public enum ConfigSource
{
    /// <summary>Autobahn's own default, from <c>Constants</c>.</summary>
    Default,

    /// <summary>Set in code, on the scenario or through a <c>With…</c> method on the runner.</summary>
    Code,

    /// <summary>Read from the JSON config file.</summary>
    JsonConfig,

    /// <summary>Read from an <c>AUTOBAHN_</c> environment variable.</summary>
    Environment,

    /// <summary>Passed as a command-line argument.</summary>
    CommandLine
}

/// <summary>
/// One setting's effective value and the layer it came from.
/// </summary>
/// <remarks>
/// "Why is the warm-up thirty seconds" should be answerable without reading three files, so
/// the resolver records where each answer came from as it works them out.
/// </remarks>
public sealed record EffectiveSetting
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public required ConfigSource Source { get; init; }
}
