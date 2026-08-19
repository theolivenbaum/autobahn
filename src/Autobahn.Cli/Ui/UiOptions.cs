using System.Net;

namespace Autobahn.Cli.Ui;

/// <summary>How the live UI is served, if it is.</summary>
public sealed record UiOptions
{
    /// <summary>0 picks a free port, which is the sane default for something that is not a service.</summary>
    public int Port { get; init; }

    /// <summary>
    /// What to bind to. Loopback unless someone said otherwise, and saying otherwise is loud.
    /// </summary>
    /// <remarks>
    /// A load-test control surface that can stop the run is not something to put on 0.0.0.0
    /// by accident, so the flag that does it is separate and prints a warning.
    /// </remarks>
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;

    /// <summary>Opens the printed URL in a browser once the server is up.</summary>
    public bool OpenBrowser { get; init; }

    /// <summary>
    /// How many intervals the host keeps for backfill.
    /// </summary>
    /// <remarks>
    /// 4,320 is twelve hours at a five-second interval. A bounded buffer rather than the
    /// whole run, because a long run's history has to live somewhere and "the load
    /// generator's heap" is the wrong answer.
    /// </remarks>
    public int HistoryCapacity { get; init; } = 4_320;

    /// <summary>
    /// How many frames a slow client may fall behind before it starts losing them.
    /// </summary>
    /// <remarks>
    /// The UI must never affect the run, so a client that cannot keep up drops frames rather
    /// than applying back-pressure to anything. It notices the gap by sequence number and
    /// backfills.
    /// </remarks>
    public int ClientQueueCapacity { get; init; } = 64;

    public static UiOptions Default { get; } = new();
}
