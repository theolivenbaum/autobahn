namespace Autobahn.Internal;

/// <summary>
/// The result of something that can fail with an <see cref="AppError"/>.
/// Validation in Autobahn returns one of these rather than throwing: an invalid load plan
/// is a user mistake to report, not an exceptional condition.
/// </summary>
internal readonly struct Result<T>
{
    private readonly T _value;
    private readonly AppError? _error;

    private Result(T value, AppError? error)
    {
        _value = value;
        _error = error;
    }

    public bool IsOk => _error is null;
    public bool IsError => _error is not null;

    public T Value => _error is null
        ? _value
        : throw new InvalidOperationException($"The result is an error: {_error.Message}");

    public AppError Error => _error
        ?? throw new InvalidOperationException("The result is not an error.");

    public static Result<T> Ok(T value) => new(value, null);
    public static Result<T> Fail(AppError error) => new(default!, error);

    public Result<TNext> Bind<TNext>(Func<T, Result<TNext>> bind) =>
        _error is null ? bind(_value) : Result<TNext>.Fail(_error);
}

/// <summary>Helpers for building and combining results.</summary>
internal static class Result
{
    /// <summary>
    /// Turns a sequence of results into a result of a sequence, keeping the first error.
    /// </summary>
    public static Result<List<T>> Sequence<T>(IEnumerable<Result<T>> results)
    {
        var items = new List<T>();

        foreach (var result in results)
        {
            if (result.IsError) return Result<List<T>>.Fail(result.Error);
            items.Add(result.Value);
        }

        return Result<List<T>>.Ok(items);
    }
}
