namespace Autobahn.Internal;

/// <summary>
/// Anything Autobahn can refuse to do, with the message the user will see.
/// Every user-facing error message lives on one of these records, so the wording stays
/// in one place instead of being scattered through the call sites that raise it.
/// </summary>
internal abstract record AppError
{
    public abstract string Message { get; }
}
