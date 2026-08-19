using Grpc.Net.Client;

namespace Autobahn.Grpc;

/// <summary>How the channels a load test uses are built.</summary>
public sealed record GrpcChannelSettings
{
    /// <summary>The largest message the client will accept. Null leaves the gRPC default.</summary>
    public int? MaxReceiveMessageSize { get; init; }

    public int? MaxSendMessageSize { get; init; }

    /// <summary>
    /// Whether one channel opens more than one HTTP/2 connection.
    /// </summary>
    /// <remarks>
    /// On by default here, unlike gRPC's own default. A single HTTP/2 connection has a
    /// concurrent-stream limit, and a load test that hits it measures its own queue rather
    /// than the server - which looks exactly like the server getting slower.
    /// </remarks>
    public bool EnableMultipleHttp2Connections { get; init; } = true;

    /// <summary>An unencrypted h2c endpoint, for a server that is not doing TLS.</summary>
    public bool AllowInsecureHttp { get; init; }

    public static GrpcChannelSettings Default { get; } = new();
}

/// <summary>
/// Builds the gRPC channels a scenario uses.
/// </summary>
/// <remarks>
/// A channel is expensive and meant to be shared; a virtual user is not a connection. So the
/// default is one channel for the run, and a pool exists for the case where each virtual user
/// really is a distinct client - a per-user credential, a per-user server affinity - and
/// sharing would make them one client with N times the traffic.
/// </remarks>
public static class GrpcChannelPool
{
    public static GrpcChannel CreateChannel(string address, GrpcChannelSettings? settings = null)
    {
        var config = settings ?? GrpcChannelSettings.Default;

        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = config.EnableMultipleHttp2Connections,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30)
        };

        var options = new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true };

        if (config.MaxReceiveMessageSize is { } receive) options.MaxReceiveMessageSize = receive;
        if (config.MaxSendMessageSize is { } send) options.MaxSendMessageSize = send;

        if (config.AllowInsecureHttp)
        {
            // Without this an h2c address is rejected before a request is ever made, with a
            // message about TLS that says nothing about the switch that fixes it.
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        }

        return GrpcChannel.ForAddress(address, options);
    }

    /// <summary>One channel per virtual user, handed out by copy index.</summary>
    public static ClientPool<GrpcChannel> CreatePool(
        string address, int count, GrpcChannelSettings? settings = null)
    {
        if (count < 1)
            throw new AutobahnException($"A gRPC channel pool of {count} channels is not something a scenario can use.");

        var pool = new ClientPool<GrpcChannel>();

        for (var i = 0; i < count; i++) pool.AddClient(CreateChannel(address, settings));

        return pool;
    }
}
