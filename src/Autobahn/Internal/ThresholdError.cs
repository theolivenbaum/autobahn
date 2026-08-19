using Autobahn.Thresholds;

namespace Autobahn.Internal;

/// <summary>Everything wrong a pass/fail rule can be, and what to tell the user about it.</summary>
internal abstract record ThresholdError : AppError
{
    /// <summary>
    /// A rule about a scenario that is not in the run. Silently never checking it would let a
    /// CI gate pass because of a typo, which is worse than not having the gate.
    /// </summary>
    public sealed record UnknownScenario(IReadOnlyList<string> Names, IReadOnlyList<string> Available) : ThresholdError
    {
        public override string Message =>
            $"Threshold{(Names.Count == 1 ? "" : "s")} reference scenario"
            + $"{(Names.Count == 1 ? "" : "s")} that this run does not have: "
            + $"{string.Join(", ", Names.Select(x => $"'{x}'"))}. "
            + $"Available: {string.Join(", ", Available.Select(x => $"'{x}'"))}.";
    }

    /// <summary>A rule whose subject means nothing for the scope it was asked for.</summary>
    public sealed record SubjectDoesNotApply(Threshold Threshold) : ThresholdError
    {
        public override string Message =>
            $"Threshold '{Threshold.Describe()}' reads {Threshold.Subject}, which is not something "
            + $"a {Threshold.Scope.ToString().ToLowerInvariant()} threshold measures.";
    }

    /// <summary>A rule that never says what it is about.</summary>
    public sealed record MissingTarget(Threshold Threshold, string What) : ThresholdError
    {
        public override string Message =>
            $"Threshold '{Threshold.Describe()}' is scoped to a {Threshold.Scope.ToString().ToLowerInvariant()} "
            + $"but names no {What}.";
    }

    /// <summary>A rule that can never fail, or can never pass.</summary>
    public sealed record ImpossibleRate(Threshold Threshold) : ThresholdError
    {
        public override string Message =>
            $"Threshold '{Threshold.Describe()}' compares a rate against {Threshold.Value}. "
            + "A rate is a share between 0 and 1, so this rule can never do anything useful.";
    }

    /// <summary>An abort policy that would fire before it had seen anything.</summary>
    public sealed record InvalidAbortAfter(Threshold Threshold) : ThresholdError
    {
        public override string Message =>
            $"Threshold '{Threshold.Describe()}' aborts after {Threshold.AbortAfter} consecutive checks. "
            + "That has to be at least 1.";
    }
}
