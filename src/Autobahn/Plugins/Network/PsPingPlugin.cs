using System.Data;
using System.Diagnostics;
using System.Net.Sockets;
using Autobahn.Stats;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Autobahn.Plugins.Network;

/// <summary>How the TCP-connect plugin probes its hosts.</summary>
public sealed record PsPingPluginConfig
{
    public required Uri[] Hosts { get; init; }

    /// <summary>Milliseconds to wait for the connection. The default is 1000.</summary>
    public int Timeout { get; init; } = 1_000;

    public static PsPingPluginConfig CreateDefault(params string[] hosts) =>
        new() { Hosts = hosts.Select(x => new Uri(x)).ToArray() };

    public static PsPingPluginConfig CreateDefault(IEnumerable<string> hosts) => CreateDefault(hosts.ToArray());
}

/// <summary>One TCP connect attempt and how long it took.</summary>
public sealed record PsPingReply
{
    public required string Status { get; init; }
    public required Uri Address { get; init; }
    public required long RoundtripTime { get; init; }
}

/// <summary>
/// Measures how long a TCP connection to each host takes, which is the part of latency ICMP
/// cannot see: a host that answers ping instantly can still be slow to accept a connection.
/// </summary>
public sealed class PsPingPlugin(PsPingPluginConfig pluginConfig) : IWorkerPlugin
{
    private const string Name = "Autobahn.Plugins.Network.PsPingPlugin";

    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    private (string Host, int Port, PsPingReply Reply)[] _pingResults = [];
    private DataSet _pluginStats = new();

    public PsPingPlugin() : this(PsPingPluginConfig.CreateDefault()) { }

    public string PluginName => Name;

    public async Task Init(IBaseContext context, IConfiguration infraConfig)
    {
        _logger = context.Logger;

        var config = infraConfig.GetSection("PsPingPlugin").Get<PsPingPluginConfig>() ?? pluginConfig;

        _logger.ZLogTrace($"PsPingPlugin config: {config}");

        try
        {
            _pingResults = await ExecPing(config).ConfigureAwait(false);
            _pluginStats = CreateStats(_pingResults);
        }
        catch (Exception ex)
        {
            _logger.ZLogError($"{ex}");
        }
    }

    public Task Start() => Task.CompletedTask;
    public Task<DataSet> GetStats(SessionStats stats) => Task.FromResult(_pluginStats);
    public Task Stop() => Task.CompletedTask;
    public void Dispose() { }

    public string[] GetHints() =>
        _pingResults
            .Where(x => x.Reply.RoundtripTime > 2L)
            .Select(x =>
                $"Physical latency to host: '{x.Host}' on port: '{x.Port}' is '{x.Reply.RoundtripTime}'. "
                + "This is bigger than 2ms which is not appropriate for load testing. "
                + "You should run your test in an environment with very small latency")
            .ToArray();

    private static async Task<(string Host, int Port, PsPingReply Reply)[]> ExecPing(PsPingPluginConfig config)
    {
        var results = new List<(string, int, PsPingReply)>(config.Hosts.Length);

        foreach (var uri in config.Hosts)
        {
            // One socket per host: a socket that has already connected cannot be reused to
            // time a connection to somewhere else.
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var timeout = new CancellationTokenSource(config.Timeout);
                await sock.ConnectAsync(uri.Host, uri.Port, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Whether the connection failed or timed out is reported through Status below.
            }

            stopwatch.Stop();

            results.Add((uri.Host, uri.Port, new PsPingReply
            {
                Status = sock.Connected ? "Connected" : "NotConnected/TimedOut",
                Address = uri,
                RoundtripTime = (long)stopwatch.Elapsed.TotalMilliseconds
            }));
        }

        return results.ToArray();
    }

    private static DataSet CreateStats((string Host, int Port, PsPingReply Reply)[] pingResults)
    {
        var stats = new DataSet();
        stats.Tables.Add(CreateTable(Name, pingResults));
        return stats;
    }

    private static DataTable CreateTable(string statsName, (string Host, int Port, PsPingReply Reply)[] pingReplies)
    {
        var table = new DataTable(statsName);

        table.Columns.AddRange(
        [
            CreateColumn("Host", "Host", typeof(string)),
            CreateColumn("Port", "Port", typeof(int)),
            CreateColumn("Status", "Status", typeof(string)),
            CreateColumn("Address", "Address", typeof(string)),
            CreateColumn("RoundTripTime", "Round Trip Time", typeof(string))
        ]);

        foreach (var (host, port, reply) in pingReplies)
        {
            var row = table.NewRow();

            row["Host"] = host;
            row["Port"] = port;
            row["Status"] = reply.Status;
            row["Address"] = reply.Address.ToString();
            row["RoundTripTime"] = $"{reply.RoundtripTime} ms";

            table.Rows.Add(row);
        }

        return table;
    }

    private static DataColumn CreateColumn(string name, string caption, Type type) =>
        new(name, type) { Caption = caption };
}
