using Autobahn.Internal;

namespace Autobahn;

/// <summary>Thrown when Autobahn refuses to run, or a run ends in an error.</summary>
public class AutobahnException : Exception
{
    public AutobahnException(string message) : base(message) { }

    internal AutobahnException(AppError error) : base(error.Message) => Error = error;

    /// <summary>The structured error behind the message, when there is one.</summary>
    internal AppError? Error { get; }
}
