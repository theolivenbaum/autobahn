using BenchmarkDotNet.Attributes;
using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Stats;

namespace Autobahn.Benchmarks;

/// <summary>
/// Turning the raw tallies into stats records. Runs once per scenario per reporting
/// interval, so it is not hot in the per-request sense - but it walks every step and reads
/// eight percentiles out of two histograms each, and it runs while the load is still going.
/// </summary>
[MemoryDiagnoser]
public class StatisticsBenchmarks
{
    private RawMeasurementStats[] _rawStats = null!;
    private LoadSimulationStats _simulationStats = null!;

    [Params(1, 10, 50)]
    public int StepCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var response = Response.Ok(statusCode: "200", sizeBytes: 1024);

        _rawStats = Enumerable.Range(0, StepCount)
            .Select(i =>
            {
                var stats = RawMeasurementStats.Empty(i == 0 ? Constants.ScenarioGlobalInfo : $"step_{i}");

                for (var m = 0; m < 1_000; m++)
                    stats.AddMeasurement(new Measurement(stats.Name, response, TimeSpan.Zero, TimeSpan.FromMilliseconds(m % 500)), 1024);

                return stats;
            })
            .ToArray();

        _simulationStats = new LoadSimulationStats { SimulationName = "keep_constant", Value = 100 };
    }

    [Benchmark]
    public ScenarioStats CreateScenarioStats() =>
        Statistics.CreateScenarioStats(
            "bench", _rawStats, _simulationStats, OperationType.Bombing,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), TimeSpan.Zero);
}
