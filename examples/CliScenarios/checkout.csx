// A whole load test in one file. Run it with:
//
//   autobahn run examples/CliScenarios/checkout.csx --out ./reports -f Json,Md
//
// No project, no build. The script's last expression is what gets run: a scenario, or a list
// of them. Everything about the run - reports, target selection, log level, thresholds from
// a config file - stays on the command line, so the same script serves three environments
// without being edited.
//
// Autobahn, Autobahn.Feeds, Autobahn.Metrics and Autobahn.Thresholds are already imported.

var catalogue = Enumerable.Range(1, 500).Select(i => $"sku-{i:D4}").ToArray();
var skus = Feed.Circular("skus", catalogue);

var browse = Scenario.Create("browse", async context =>
    {
        var sku = skus.Next();

        await Task.Delay(Random.Shared.Next(10, 40), context.CancellationToken);

        return Response.Ok(statusCode: "200", sizeBytes: sku.Length * 64);
    })
    .WithWeight(80)
    .WithoutWarmUp()
    .WithLoadSimulations(
        Simulation.RampingInject(rate: 60, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(5)),
        Simulation.Inject(rate: 60, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(10)));

var checkout = Scenario.Create("checkout", async context =>
    {
        context.Metrics.Counter("orders").Increment();

        await Step.Run("reserve", context, async () =>
        {
            await Task.Delay(20, context.CancellationToken);
            return Response.Ok(statusCode: "200");
        });

        return await Step.Run("pay", context, async () =>
        {
            await Task.Delay(40, context.CancellationToken);
            return Response.Ok(statusCode: "201");
        });
    })
    .WithWeight(20)
    .WithoutWarmUp()
    .WithLoadSimulations(
        Simulation.RampingInject(rate: 60, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(5)),
        Simulation.Inject(rate: 60, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(10)));

return new[] { browse, checkout };
