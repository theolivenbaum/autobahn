using Autobahn;

// A load test is an ordinary .NET program: describe the work, describe the load, run it.

var scenario = Scenario.Create("hello_world_scenario", async context =>
    {
        // Put any call you want to measure here - HTTP, SQL, gRPC, a queue publish.
        // Autobahn times it and records whether it succeeded.
        await Task.Delay(100);

        return Response.Ok(statusCode: "200", sizeBytes: 1_024);
    })
    .WithoutWarmUp()
    .WithLoadSimulations(
        // Ramp the arrival rate up, then hold it. Open model: the rate does not sag
        // when the target slows down, so the numbers say what the target could take.
        Simulation.RampingInject(rate: 20, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(10)),
        Simulation.Inject(rate: 20, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(20)));

var stats = AutobahnRunner
    .RegisterScenarios(scenario)
    .WithTestSuite("examples")
    .WithTestName("hello world")
    .Run(args);

Console.WriteLine($"ok: {stats.AllOkCount}, failed: {stats.AllFailCount}, duration: {stats.Duration}");
