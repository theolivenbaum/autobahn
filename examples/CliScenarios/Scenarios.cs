using Autobahn;
using Autobahn.Thresholds;

namespace Autobahn.Examples.CliScenarios;

/// <summary>
/// Scenarios exposed for the <c>autobahn</c> command line rather than run by a Main of their
/// own. Build this project and point the tool at the assembly:
/// <code>
/// dotnet build examples/Examples.slnx
/// autobahn list examples/CliScenarios/bin/Debug/net10.0/CliScenarios.dll
/// autobahn run  examples/CliScenarios/bin/Debug/net10.0/CliScenarios.dll -t read --out ./reports
/// </code>
/// </summary>
/// <remarks>
/// A scenario source is a public static property, or a public static parameterless method,
/// returning <see cref="ScenarioProps"/> or a sequence of them. Marking them
/// <c>[ScenarioSource]</c> is optional but says which members you meant, so a public helper
/// that happens to return a scenario is not mistaken for one.
/// </remarks>
public static class Scenarios
{
    [ScenarioSource]
    public static ScenarioProps Read =>
        Scenario.Create("read", async context =>
            {
                await Task.Delay(Random.Shared.Next(5, 25), context.CancellationToken);
                return Response.Ok(statusCode: "200", sizeBytes: 2_048);
            })
            .WithoutWarmUp()
            .WithIterationTimeout(TimeSpan.FromSeconds(2))
            .WithLoadSimulations(
                Simulation.RampingInject(rate: 50, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(5)),
                Simulation.Inject(rate: 50, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(10)));

    [ScenarioSource]
    public static ScenarioProps Write =>
        Scenario.Create("write", async context =>
            {
                context.Metrics.Counter("writes").Increment();

                await Task.Delay(Random.Shared.Next(20, 60), context.CancellationToken);
                return Response.Ok(statusCode: "201", sizeBytes: 256);
            })
            .WithoutWarmUp()
            .WithLoadSimulations(
                Simulation.RampingInject(rate: 10, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(5)),
                Simulation.Inject(rate: 10, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(10)));

    /// <summary>A source can hand back several scenarios at once.</summary>
    [ScenarioSource]
    public static IEnumerable<ScenarioProps> Smoke()
    {
        foreach (var name in new[] { "smoke_a", "smoke_b" })
        {
            yield return Scenario.Create(name, async context =>
                {
                    await Task.Delay(5, context.CancellationToken);
                    return Response.Ok();
                })
                .WithoutWarmUp()
                .WithLoadSimulations(Simulation.IterationsForConstant(copies: 2, iterations: 50));
        }
    }

    /// <summary>
    /// Not marked, and so not discovered while the marked ones exist - which is the point of
    /// marking. It is here to show the rule rather than to be run.
    /// </summary>
    public static ScenarioProps Helper() =>
        Scenario.Create("helper", _ => Task.FromResult<IResponse>(Response.Ok()))
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(1, TimeSpan.FromSeconds(5)));
}
