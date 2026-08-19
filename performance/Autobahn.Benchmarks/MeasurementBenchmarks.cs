using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Autobahn.Internal.Domain;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Stats;

namespace Autobahn.Benchmarks;

/// <summary>
/// The measurement path: what every step of every iteration costs.
/// </summary>
/// <remarks>
/// The number that matters here is allocation, not time. Publishing a measurement should
/// allocate nothing at all - the mailbox message is a struct - because a load generator
/// that allocates per request reports its own GC as the target's latency.
/// </remarks>
[MemoryDiagnoser]
public class MeasurementBenchmarks
{
    private const int MeasurementCount = 10_000;

    private ScenarioStatsActor _actor = null!;
    private RawMeasurementStats _rawStats = null!;
    private IResponse _okResponse = null!;

    [GlobalSetup]
    public void Setup()
    {
        var scenario = ScenarioFactory.CreateScenario(
            Scenario.Create("bench", _ => Task.FromResult<IResponse>(Response.Ok()))).Value;

        _actor = new ScenarioStatsActor(NullLogger.Instance, scenario, TimeSpan.FromSeconds(5));
        _rawStats = RawMeasurementStats.Empty("step");
        _okResponse = Response.Ok(statusCode: "200", sizeBytes: 1024);
    }

    [GlobalCleanup]
    public void Cleanup() => _actor.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Just the enqueue: what the scenario's own thread pays.</summary>
    [Benchmark(OperationsPerInvoke = MeasurementCount)]
    public void PublishMeasurement()
    {
        for (var i = 0; i < MeasurementCount; i++)
            _actor.AddMeasurement(new Measurement("step", _okResponse, TimeSpan.Zero, TimeSpan.FromMilliseconds(10)));
    }

    /// <summary>Folding one measurement into the tally: what the actor's own thread pays.</summary>
    [Benchmark(OperationsPerInvoke = MeasurementCount)]
    public void AccumulateMeasurement()
    {
        for (var i = 0; i < MeasurementCount; i++)
        {
            _rawStats.AddMeasurement(
                new Measurement("step", _okResponse, TimeSpan.Zero, TimeSpan.FromMilliseconds(10)), 1024);
        }
    }

    /// <summary>Publish plus drain plus build - one whole reporting interval.</summary>
    [Benchmark]
    public async Task<ScenarioStats> PublishAndBuildInterval()
    {
        for (var i = 0; i < MeasurementCount; i++)
        {
            _actor.AddMeasurement(new Measurement(
                Constants.ScenarioGlobalInfo, _okResponse, TimeSpan.Zero, TimeSpan.FromMilliseconds(10)));
        }

        return await _actor.BuildReportingStats(
            new LoadSimulationStats { SimulationName = "keep_constant", Value = 100 }, TimeSpan.FromSeconds(5));
    }
}
