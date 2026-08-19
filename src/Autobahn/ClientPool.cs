namespace Autobahn;

/// <summary>
/// A fixed set of clients shared across scenario copies, handed out by copy index so each
/// copy consistently uses the same one.
/// </summary>
public sealed class ClientPool<T> : IDisposable
{
    private readonly List<T> _clients = [];
    private bool _disposed;

    public IReadOnlyList<T> Clients => _clients;

    public void AddClient(T client) => _clients.Add(client);

    public T GetClient(ScenarioInfo scenarioInfo) => _clients[scenarioInfo.ThreadNumber % _clients.Count];

    public void DisposeClients()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var client in _clients)
            if (client is IDisposable disposable) disposable.Dispose();
    }

    /// <summary>Disposes every client with a caller-supplied teardown, for clients that are not IDisposable.</summary>
    public void DisposeClients(Action<T> disposeClient)
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var client in _clients) disposeClient(client);
    }

    public void Dispose() => DisposeClients();
}
