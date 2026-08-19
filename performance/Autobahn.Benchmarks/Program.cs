using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Autobahn.Benchmarks.BenchmarkEntryPoint).Assembly).Run(args);

namespace Autobahn.Benchmarks
{
    /// <summary>Marks the assembly for <see cref="BenchmarkSwitcher"/> to scan.</summary>
    internal sealed class BenchmarkEntryPoint;
}
