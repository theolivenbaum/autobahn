namespace Autobahn.Internal.Domain;

/// <summary>The canned responses the engine produces when user code did not produce one.</summary>
internal static class ResponseInternal
{
    public static Response<object> OkEmpty() => new()
    {
        StatusCode = "", IsError = false, SizeBytes = 0, LatencyMs = 0, Message = ""
    };

    public static Response<T> FailEmpty<T>() => new()
    {
        StatusCode = "", IsError = true, SizeBytes = 0, LatencyMs = 0, Message = ""
    };

    public static Response<T> FailUnhandled<T>(Exception ex) => new()
    {
        StatusCode = Constants.UnhandledExceptionCode,
        IsError = true,
        SizeBytes = 0,
        LatencyMs = 0,
        Message = ex.Message
    };

    /// <summary>An iteration Autobahn stopped waiting for, distinct from a cancelled operation.</summary>
    public static Response<T> FailIterationTimeout<T>() => new()
    {
        StatusCode = Constants.IterationTimeoutStatusCode,
        IsError = true,
        SizeBytes = 0,
        LatencyMs = 0,
        Message = Constants.IterationTimeoutMessage
    };

    public static Response<T> FailTimeout<T>() => new()
    {
        StatusCode = Constants.TimeoutStatusCode,
        IsError = true,
        SizeBytes = 0,
        LatencyMs = 0,
        Message = Constants.OperationTimeoutMessage
    };
}
