using System.Data;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ZLogger;
using Autobahn.Stats;

namespace Autobahn.Plugins.Network;

/// <summary>How the ping plugin probes its hosts.</summary>
public sealed record PingPluginConfig
{
    public required string[] Hosts { get; init; }

    /// <summary>
    /// Bytes of payload to send, 1 to 65500. The default is 32. Over about 1386 bytes the
    /// packet fragments on Ethernet, which changes what the round trip measures.
    /// </summary>
    public int BufferSizeBytes { get; init; } = 32;

    /// <summary>How many hops the packet may take before it is discarded. The default is 128.</summary>
    public int Ttl { get; init; } = 128;

    /// <summary>
    /// When true the packet may not be fragmented, so a host behind a smaller MTU fails with
    /// PacketTooBig instead of succeeding. Useful for finding the path MTU. The default is false.
    /// </summary>
    public bool DontFragment { get; init; }

    /// <summary>Milliseconds to wait for a reply. The default is 1000.</summary>
    public int Timeout { get; init; } = 1_000;

    public static PingPluginConfig CreateDefault(params string[] hosts) => new() { Hosts = hosts };

    public static PingPluginConfig CreateDefault(IEnumerable<string> hosts) => CreateDefault(hosts.ToArray());
}

/// <summary>
/// Pings the hosts under test before the run and reports the physical latency to them, so a
/// report can show whether the network was ever capable of the numbers being measured.
/// </summary>
public sealed class PingPlugin(PingPluginConfig pluginConfig) : IWorkerPlugin
{
    private const string Name = "Autobahn.Plugins.Network.PingPlugin";

    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private (string Host, PingReply Reply)[] _pingResults = [];
    private DataSet _pluginStats = new();

    public PingPlugin() : this(PingPluginConfig.CreateDefault()) { }

    public string PluginName => Name;

    public Task Init(IBaseContext context, IConfiguration infraConfig)
    {
        _logger = context.Logger;

        var config = infraConfig.GetSection("PingPlugin").Get<PingPluginConfig>() ?? pluginConfig;

        _logger.ZLogTrace($"PingPlugin config: {config}");

        try
        {
            _pingResults = ExecPing(config);
            _pluginStats = CreateStats(config, _pingResults);
        }
        catch (Exception ex)
        {
            // A plugin that cannot reach its hosts reports nothing; it does not fail the run.
            _logger.ZLogError($"{ex}");
        }

        return Task.CompletedTask;
    }

    public Task Start() => Task.CompletedTask;
    public Task<DataSet> GetStats(SessionStats stats) => Task.FromResult(_pluginStats);
    public Task Stop() => Task.CompletedTask;
    public void Dispose() { }

    public string[] GetHints() =>
        _pingResults
            .Where(x => x.Reply.RoundtripTime > 2L)
            .Select(x =>
                $"Physical latency to host: '{x.Host}' is '{x.Reply.RoundtripTime}'. This is bigger than 2ms which is "
                + "not appropriate for load testing. You should run your test in an environment with very small latency")
            .ToArray();

    private static (string Host, PingReply Reply)[] ExecPing(PingPluginConfig config)
    {
        var pingOptions = new PingOptions { Ttl = config.Ttl, DontFragment = config.DontFragment };
        using var ping = new Ping();

        var buffer = Encoding.ASCII.GetBytes(new string('-', config.BufferSizeBytes));

        return config.Hosts
            .Select(host => (host, ping.Send(host, config.Timeout, buffer, pingOptions)))
            .ToArray();
    }

    private static DataSet CreateStats(PingPluginConfig config, (string Host, PingReply Reply)[] pingResults)
    {
        var stats = new DataSet();
        stats.Tables.Add(CreateTable(Name, config, pingResults));
        return stats;
    }

    private static DataTable CreateTable(
        string statsName, PingPluginConfig config, (string Host, PingReply Reply)[] pingReplies)
    {
        var table = new DataTable(statsName);

        table.Columns.AddRange(
        [
            CreateColumn("Host", "Host"),
            CreateColumn("Status", "Status"),
            CreateColumn("Address", "Address"),
            CreateColumn("RoundTripTime", "Round Trip Time"),
            CreateColumn("Ttl", "Time to Live"),
            CreateColumn("DontFragment", "Don't Fragment"),
            CreateColumn("BufferSize", "Buffer Size")
        ]);

        foreach (var (host, reply) in pingReplies)
        {
            var row = table.NewRow();

            row["Host"] = host;
            row["Status"] = reply.Status.ToString();
            row["Address"] = reply.Address?.ToString() ?? "";
            row["RoundTripTime"] = $"{reply.RoundtripTime} ms";
            row["Ttl"] = config.Ttl.ToString();
            row["DontFragment"] = config.DontFragment.ToString();
            row["BufferSize"] = $"{config.BufferSizeBytes} bytes";

            table.Rows.Add(row);
        }

        return table;
    }

    private static DataColumn CreateColumn(string name, string caption) =>
        new(name, typeof(string)) { Caption = caption };
}
