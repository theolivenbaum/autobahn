using Autobahn.Thresholds;

namespace Autobahn.Internal.Domain.Thresholds;

/// <summary>
/// Checks the rules themselves before the run starts.
/// </summary>
/// <remarks>
/// A gate that silently never checks anything is worse than no gate, so a rule that cannot
/// mean what it says - a scenario that is not in the run, a subject that does not apply to
/// its scope, a rate compared against 12 - fails the run up front rather than passing
/// quietly at the end.
/// </remarks>
internal static class ThresholdValidation
{
    private static readonly HashSet<ThresholdSubject> MeasurementSubjects =
    [
        ThresholdSubject.ErrorRate, ThresholdSubject.OkRate,
        ThresholdSubject.RequestCount, ThresholdSubject.OkCount, ThresholdSubject.FailCount,
        ThresholdSubject.Rps,
        ThresholdSubject.MinLatency, ThresholdSubject.MeanLatency, ThresholdSubject.MaxLatency,
        ThresholdSubject.Percent50, ThresholdSubject.Percent75,
        ThresholdSubject.Percent95, ThresholdSubject.Percent99,
        ThresholdSubject.AllBytes
    ];

    private static readonly HashSet<ThresholdSubject> StatusCodeSubjects =
        [ThresholdSubject.StatusCodeCount, ThresholdSubject.StatusCodeRate];

    private static readonly HashSet<ThresholdSubject> MetricSubjects =
    [
        ThresholdSubject.MetricCurrent, ThresholdSubject.MetricMin, ThresholdSubject.MetricMean,
        ThresholdSubject.MetricMax, ThresholdSubject.MetricPercent50,
        ThresholdSubject.MetricPercent95, ThresholdSubject.MetricPercent99, ThresholdSubject.MetricCount
    ];

    private static readonly HashSet<ThresholdSubject> RateSubjects =
        [ThresholdSubject.ErrorRate, ThresholdSubject.OkRate, ThresholdSubject.StatusCodeRate];

    public static Result<IReadOnlyList<Threshold>> Check(
        IReadOnlyList<Threshold> thresholds, IReadOnlyList<string> scenarioNames)
    {
        var unknown = ThresholdChecker.FindUnknownScenarios(thresholds, scenarioNames);

        if (unknown.Count > 0)
            return Result<IReadOnlyList<Threshold>>.Fail(new ThresholdError.UnknownScenario(unknown, scenarioNames));

        foreach (var threshold in thresholds)
        {
            var error = CheckOne(threshold);
            if (error is not null) return Result<IReadOnlyList<Threshold>>.Fail(error);
        }

        return Result<IReadOnlyList<Threshold>>.Ok(thresholds);
    }

    private static AppError? CheckOne(Threshold threshold)
    {
        var allowed = threshold.Scope switch
        {
            ThresholdScope.Scenario or ThresholdScope.Step => MeasurementSubjects,
            ThresholdScope.StatusCode => StatusCodeSubjects,
            ThresholdScope.Metric => MetricSubjects,
            _ => MeasurementSubjects
        };

        if (!allowed.Contains(threshold.Subject))
            return new ThresholdError.SubjectDoesNotApply(threshold);

        if (threshold.Scope == ThresholdScope.Step && string.IsNullOrWhiteSpace(threshold.StepName))
            return new ThresholdError.MissingTarget(threshold, "step");

        if (threshold.Scope == ThresholdScope.StatusCode && string.IsNullOrWhiteSpace(threshold.StatusCode))
            return new ThresholdError.MissingTarget(threshold, "status code");

        if (threshold.Scope == ThresholdScope.Metric && string.IsNullOrWhiteSpace(threshold.MetricName))
            return new ThresholdError.MissingTarget(threshold, "metric");

        if (RateSubjects.Contains(threshold.Subject) && threshold.Value is < 0.0 or > 1.0)
            return new ThresholdError.ImpossibleRate(threshold);

        if (threshold.AbortAfter is { } abortAfter && abortAfter < 1)
            return new ThresholdError.InvalidAbortAfter(threshold);

        return null;
    }
}
