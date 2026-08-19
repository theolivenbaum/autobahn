using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Autobahn.Cli;

/// <summary>
/// Compiles and runs a C# script, and takes the scenarios it hands back.
/// </summary>
/// <remarks>
/// This is the shortest path there is from "I want to hammer this endpoint" to results: one
/// file, no project, no build. The script's job is to describe the scenarios and return them;
/// everything about the run - reports, thresholds, target selection - stays on the command
/// line, so the same script can be run three different ways without editing it.
/// </remarks>
internal static class ScriptScenarioLoader
{
    /// <summary>The file extensions treated as a script rather than as an assembly.</summary>
    public static bool IsScript(string path) =>
        Path.GetExtension(path) is ".cs" or ".csx";

    public static async Task<IReadOnlyList<ScenarioProps>> LoadAsync(string scriptPath)
    {
        var fullPath = Path.GetFullPath(scriptPath);

        if (!File.Exists(fullPath))
            throw new AutobahnException($"Script not found: '{scriptPath}'.");

        var code = await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);

        var options = ScriptOptions.Default
            .WithFilePath(fullPath)
            // The script gets the same assemblies the tool is running with, so it can reach
            // anything Autobahn already depends on without a #r directive.
            .WithReferences(ReferencedAssemblies())
            .WithImports(
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "System.Net.Http",
                "System.Threading",
                "System.Threading.Tasks",
                "Autobahn",
                "Autobahn.Feeds",
                "Autobahn.Metrics",
                "Autobahn.Thresholds")
            // Scripts live next to their data; a relative path in one should mean relative to
            // the script, not to wherever the tool was invoked.
            .WithSourceResolver(new SourceFileResolver([], Path.GetDirectoryName(fullPath)))
            .WithMetadataResolver(ScriptMetadataResolver.Default.WithBaseDirectory(Path.GetDirectoryName(fullPath)!));

        object? result;

        try
        {
            result = await CSharpScript.EvaluateAsync<object?>(code, options).ConfigureAwait(false);
        }
        catch (CompilationErrorException ex)
        {
            throw new AutobahnException(
                $"Could not compile '{Path.GetFileName(fullPath)}':{Environment.NewLine}"
                + string.Join(Environment.NewLine, ex.Diagnostics));
        }

        return result switch
        {
            ScenarioProps single => [single],
            IEnumerable<ScenarioProps> many => many.ToArray(),
            null => throw new AutobahnException(
                $"'{Path.GetFileName(fullPath)}' returned nothing. A script has to end with the scenario, or "
                + "the list of scenarios, it wants run - the last expression is its result."),
            _ => throw new AutobahnException(
                $"'{Path.GetFileName(fullPath)}' returned a {result.GetType().Name}. A script has to return "
                + "a ScenarioProps or a sequence of them.")
        };
    }

    /// <summary>
    /// Everything currently loaded that has a file behind it.
    /// </summary>
    /// <remarks>
    /// Dynamic assemblies and anything loaded from memory have no location and cannot be
    /// referenced by the compiler, so they are skipped rather than throwing.
    /// </remarks>
    private static IEnumerable<Assembly> ReferencedAssemblies() =>
        AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(x => !x.IsDynamic && !string.IsNullOrEmpty(x.Location));
}
