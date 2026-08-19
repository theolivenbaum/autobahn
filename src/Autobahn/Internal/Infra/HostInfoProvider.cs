using System.Reflection;
using System.Runtime.Versioning;
using Autobahn.Stats;

namespace Autobahn.Internal.Infra;

/// <summary>Describes the machine the run is executing on.</summary>
internal static class HostInfoProvider
{
    public static HostInfo Init(Version? autobahnVersion = null)
    {
        var version = autobahnVersion
                      ?? typeof(HostInfoProvider).Assembly.GetName().Version
                      ?? new Version(0, 0, 0);

        var processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");

        return new HostInfo
        {
            MachineName = Environment.MachineName,
            CurrentOperation = OperationType.None,
            OS = Environment.OSVersion.ToString(),
            DotNetVersion = GetDotNetVersion(),
            Processor = processor ?? string.Empty,
            CoresCount = Environment.ProcessorCount,
            AutobahnVersion = $"{version.Major}.{version.Minor}.{version.Build}"
        };
    }

    private static string GetDotNetVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetCallingAssembly();
        return assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName
               ?? Environment.Version.ToString();
    }

    /// <summary>Whether there is an interactive console to draw a live table on.</summary>
    public static ApplicationType GetApplicationType()
    {
        try
        {
            return Console.WindowHeight <= 0 ? ApplicationType.Process : ApplicationType.Console;
        }
        catch
        {
            return ApplicationType.Process;
        }
    }

    public static string CreateSessionId()
    {
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd_HH.mm.ff");
        var guid = Guid.NewGuid().GetHashCode().ToString("x");
        return $"{date}_session_{guid}";
    }
}
