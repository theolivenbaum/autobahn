using System.Reflection;

namespace Autobahn.Internal.Infra;

/// <summary>Reads the HTML report's embedded assets out of the Autobahn assembly.</summary>
internal static class ResourceManager
{
    public static string? ReadResource(string name)
    {
        var assembly = typeof(ResourceManager).Assembly;

        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(x => x.Contains(name));
        if (resourceName is null) return null;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
