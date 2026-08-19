using Microsoft.Extensions.Configuration;
using Autobahn.Plugins.Network;

namespace Autobahn.Tests;

public class ResponseTests
{
    [Test]
    public async Task Ok_defaults_to_no_status_no_size_and_no_message()
    {
        var response = Response.Ok();

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.StatusCode).IsEqualTo("");
        await Assert.That(response.SizeBytes).IsEqualTo(0L);
        await Assert.That(response.LatencyMs).IsEqualTo(0.0);
        await Assert.That(response.Message).IsEqualTo("");
        await Assert.That(response.Payload).IsNull();
    }

    [Test]
    public async Task Fail_is_an_error()
    {
        await Assert.That(Response.Fail().IsError).IsTrue();
        await Assert.That(Response.FailOf<int>().IsError).IsTrue();
        await Assert.That(Response.OkOf<int>().IsError).IsFalse();
    }

    [Test]
    public async Task A_payload_travels_with_the_response()
    {
        var response = Response.Ok(payload: new[] { 1, 2, 3 }, statusCode: "200", sizeBytes: 12);

        await Assert.That(response.Payload).IsEquivalentTo(new[] { 1, 2, 3 });
        await Assert.That(response.StatusCode).IsEqualTo("200");
        await Assert.That(response.SizeBytes).IsEqualTo(12L);
    }

    [Test]
    public async Task A_null_message_is_normalised_to_empty()
    {
        await Assert.That(Response.Ok(message: null!).Message).IsEqualTo("");
        await Assert.That(Response.Fail(message: null!).Message).IsEqualTo("");
    }
}

public class ScenarioApiTests
{
    private static ScenarioProps Scn() => Scenario.Create("s", _ => Task.FromResult<IResponse>(Response.Ok()));

    [Test]
    public async Task A_new_scenario_warms_up_and_keeps_one_copy_by_default()
    {
        var scenario = Scn();

        await Assert.That(scenario.WarmUpDuration).IsEqualTo(Constants.DefaultWarmUpDuration);
        await Assert.That(scenario.RestartIterationOnFail).IsTrue();
        await Assert.That(scenario.MaxFailCount).IsEqualTo(Constants.ScenarioMaxFailCount);
        await Assert.That(scenario.LoadSimulations.Count).IsEqualTo(1);
        await Assert.That(scenario.LoadSimulations[0])
            .IsEqualTo(Simulation.KeepConstant(Constants.DefaultCopiesCount, Constants.DefaultSimulationDuration));
    }

    [Test]
    public async Task An_empty_scenario_has_no_run_function_and_no_warm_up()
    {
        var scenario = Scenario.Empty("s");

        await Assert.That(scenario.Run).IsNull();
        await Assert.That(scenario.WarmUpDuration).IsNull();
    }

    [Test]
    public async Task The_fluent_methods_return_a_new_scenario_and_leave_the_original_alone()
    {
        var original = Scn();
        var modified = original.WithoutWarmUp().WithMaxFailCount(7).WithRestartIterationOnFail(false);

        await Assert.That(original.WarmUpDuration).IsNotNull();
        await Assert.That(original.MaxFailCount).IsEqualTo(Constants.ScenarioMaxFailCount);

        await Assert.That(modified.WarmUpDuration).IsNull();
        await Assert.That(modified.MaxFailCount).IsEqualTo(7);
        await Assert.That(modified.RestartIterationOnFail).IsFalse();
    }

    [Test]
    public async Task The_runner_methods_return_a_new_context_and_leave_the_original_alone()
    {
        var original = AutobahnRunner.RegisterScenarios(Scn());
        var modified = original.WithTestSuite("suite").WithoutReports();

        await Assert.That(original.TestSuite).IsEqualTo(Constants.DefaultTestSuite);
        await Assert.That(original.Reporting.Formats).IsNotEmpty();

        await Assert.That(modified.TestSuite).IsEqualTo("suite");
        await Assert.That(modified.Reporting.Formats).IsEmpty();
    }
}

public class ClientPoolTests
{
    private sealed class FakeClient : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private static ScenarioInfo Info(int threadNumber) => new()
    {
        ThreadId = $"s_{threadNumber}",
        ThreadNumber = threadNumber,
        ScenarioName = "s",
        ScenarioDuration = TimeSpan.Zero,
        ScenarioOperation = ScenarioOperation.Bombing
    };

    [Test]
    public async Task A_scenario_copy_always_gets_the_same_client()
    {
        using var pool = new ClientPool<string>();
        pool.AddClient("a");
        pool.AddClient("b");

        await Assert.That(pool.GetClient(Info(0))).IsEqualTo("a");
        await Assert.That(pool.GetClient(Info(1))).IsEqualTo("b");
        await Assert.That(pool.GetClient(Info(2))).IsEqualTo("a");
        await Assert.That(pool.GetClient(Info(2))).IsEqualTo("a");
    }

    [Test]
    public async Task Disposing_the_pool_disposes_every_disposable_client_once()
    {
        var first = new FakeClient();
        var second = new FakeClient();

        var pool = new ClientPool<FakeClient>();
        pool.AddClient(first);
        pool.AddClient(second);

        pool.Dispose();
        pool.Dispose();

        await Assert.That(first.Disposed).IsTrue();
        await Assert.That(second.Disposed).IsTrue();
    }

    [Test]
    public async Task A_client_that_is_not_disposable_can_be_torn_down_by_hand()
    {
        var disposed = new List<string>();

        var pool = new ClientPool<string>();
        pool.AddClient("a");
        pool.AddClient("b");

        pool.DisposeClients(disposed.Add);

        await Assert.That(disposed).IsEquivalentTo(new List<string> { "a", "b" });
    }
}

public class PingPluginConfigTests
{
    [Test]
    public async Task The_ping_plugin_defaults_match_what_is_documented()
    {
        var config = PingPluginConfig.CreateDefault("localhost");

        await Assert.That(config.Hosts).IsEquivalentTo(new[] { "localhost" });
        await Assert.That(config.BufferSizeBytes).IsEqualTo(32);
        await Assert.That(config.Ttl).IsEqualTo(128);
        await Assert.That(config.DontFragment).IsFalse();
        await Assert.That(config.Timeout).IsEqualTo(1_000);
    }

    [Test]
    public async Task The_ping_plugin_config_binds_from_an_infra_config_section()
    {
        var infra = new ConfigurationBuilder()
            .AddJsonFile("Assets/Configuration/infra_config.json")
            .Build();

        var config = infra.GetSection("PingPlugin").Get<PingPluginConfig>()!;

        await Assert.That(config.Hosts).IsEquivalentTo(new[] { "localhost" });
        await Assert.That(config.Timeout).IsEqualTo(500);
    }

    [Test]
    public async Task A_ping_plugin_that_has_not_run_reports_nothing_rather_than_throwing()
    {
        using var plugin = new PingPlugin();

        await Assert.That(plugin.GetHints()).IsEmpty();
        await Assert.That((await plugin.GetStats(Stats.SessionStats.Empty)).Tables.Count).IsEqualTo(0);
        await Assert.That(plugin.PluginName).IsNotEmpty();
    }

    [Test]
    public async Task A_tcp_ping_plugin_that_has_not_run_reports_nothing_rather_than_throwing()
    {
        using var plugin = new PsPingPlugin();

        await Assert.That(plugin.GetHints()).IsEmpty();
        await Assert.That((await plugin.GetStats(Stats.SessionStats.Empty)).Tables.Count).IsEqualTo(0);
        await Assert.That(plugin.PluginName).IsNotEmpty();
    }

    [Test]
    public async Task The_tcp_ping_plugin_turns_host_strings_into_uris()
    {
        var config = PsPingPluginConfig.CreateDefault("tcp://localhost:5000");

        await Assert.That(config.Hosts.Length).IsEqualTo(1);
        await Assert.That(config.Hosts[0].Host).IsEqualTo("localhost");
        await Assert.That(config.Hosts[0].Port).IsEqualTo(5000);
    }
}
