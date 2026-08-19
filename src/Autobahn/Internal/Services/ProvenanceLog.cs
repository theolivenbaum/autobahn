using Autobahn.Configuration;

namespace Autobahn.Internal.Services;

/// <summary>
/// Records where each effective setting's value came from, as the resolver works it out.
/// </summary>
/// <remarks>
/// Written to as a side effect of resolution rather than reconstructed afterwards, because
/// reconstructing it means writing the precedence rules a second time and having the two
/// drift. Whatever the resolver actually did is what this says it did.
/// </remarks>
internal sealed class ProvenanceLog
{
    private readonly List<EffectiveSetting> _settings = [];

    public IReadOnlyList<EffectiveSetting> Settings => _settings;

    /// <summary>Records a resolved value and hands it straight back, so it can wrap an expression.</summary>
    public T Record<T>(string name, T value, ConfigSource source)
    {
        _settings.Add(new EffectiveSetting
        {
            Name = name,
            Value = Describe(value),
            Source = source
        });

        return value;
    }

    private static string Describe(object? value) => value switch
    {
        null => "",
        string s => s,
        System.Collections.IEnumerable list and not string =>
            list.Cast<object?>().ToArray() is { Length: > 0 } items ? string.Join(", ", items) : "(none)",
        _ => value.ToString() ?? ""
    };
}
