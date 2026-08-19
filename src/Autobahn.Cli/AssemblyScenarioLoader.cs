using System.Reflection;
using System.Runtime.Loader;

namespace Autobahn.Cli;

/// <summary>
/// Finds the scenarios in a built assembly.
/// </summary>
/// <remarks>
/// A load test is normally a program with a <c>Main</c> that calls the runner. That is still
/// the supported shape, but it means the command line cannot change anything about the run
/// without the program having thought to pass its arguments through. So this takes the other
/// route: the assembly exposes its scenarios as static members, the CLI builds the run around
/// them, and every option on the command line applies.
/// </remarks>
internal static class AssemblyScenarioLoader
{
    /// <summary>Loads the assembly and returns everything in it that produces scenarios.</summary>
    public static IReadOnlyList<ScenarioProps> Load(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);

        if (!File.Exists(fullPath))
            throw new AutobahnException($"Assembly not found: '{assemblyPath}'.");

        var context = new ScenarioLoadContext(fullPath);
        var assembly = context.LoadFromAssemblyPath(fullPath);

        var members = FindMembers(assembly);

        if (members.Count == 0)
        {
            throw new AutobahnException(
                $"No scenarios found in '{Path.GetFileName(fullPath)}'. A scenario source is a public static "
                + "property, or a public static parameterless method, returning ScenarioProps or a sequence of "
                + "them - optionally marked [ScenarioSource].");
        }

        return members.SelectMany(Invoke).ToArray();
    }

    /// <summary>
    /// The members that produce scenarios: those marked <see cref="ScenarioSourceAttribute"/>
    /// if any are, otherwise every public static member of the right shape.
    /// </summary>
    private static List<MemberInfo> FindMembers(Assembly assembly)
    {
        var candidates = new List<MemberInfo>();

        foreach (var type in GetLoadableTypes(assembly))
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;

            candidates.AddRange(type.GetProperties(flags).Where(p => Produces(p.PropertyType)));

            candidates.AddRange(type
                .GetMethods(flags)
                .Where(m => m.GetParameters().Length == 0 && !m.IsSpecialName && Produces(m.ReturnType)));
        }

        var marked = candidates.Where(x => x.GetCustomAttribute<ScenarioSourceAttribute>() is not null).ToList();

        // An assembly that marks any of its scenarios has said which ones it meant.
        return marked.Count > 0 ? marked : candidates;
    }

    /// <summary>
    /// A type that cannot be loaded - usually a dependency the target does not ship - is
    /// skipped rather than failing the whole discovery, because the scenarios are rarely in it.
    /// </summary>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private static bool Produces(Type type) =>
        type == typeof(ScenarioProps) || typeof(IEnumerable<ScenarioProps>).IsAssignableFrom(type);

    private static IEnumerable<ScenarioProps> Invoke(MemberInfo member)
    {
        var value = member switch
        {
            PropertyInfo property => property.GetValue(null),
            MethodInfo method => method.Invoke(null, null),
            _ => null
        };

        return value switch
        {
            ScenarioProps single => [single],
            IEnumerable<ScenarioProps> many => many,
            _ => []
        };
    }

    /// <summary>
    /// Loads the target beside its own dependencies rather than the CLI's.
    /// </summary>
    /// <remarks>
    /// The tool ships its own copy of Autobahn and of everything under it. Without a resolver
    /// pointed at the target's <c>.deps.json</c>, a target built against a different version of
    /// any of them fails to load with a message about a missing assembly rather than one about
    /// a version. Autobahn itself is deliberately *not* redirected: the CLI and the target have
    /// to agree on the <c>ScenarioProps</c> type, or nothing discovered here could be run.
    /// </remarks>
    private sealed class ScenarioLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is "Autobahn") return null;

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
