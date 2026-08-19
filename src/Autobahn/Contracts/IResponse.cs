namespace Autobahn;

/// <summary>The untyped view of what a step or scenario returned.</summary>
public interface IResponse
{
    string StatusCode { get; }
    bool IsError { get; }
    long SizeBytes { get; }

    /// <summary>Latency the client measured itself. Zero means "let Autobahn time it".</summary>
    double LatencyMs { get; }

    string Message { get; }
}
