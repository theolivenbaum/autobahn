using Autobahn.Stats;
using Autobahn.Thresholds;

namespace Autobahn.Internal.Domain.Thresholds;

/// <summary>
/// Reads the one number a threshold is about out of the stats it is scoped to.
/// </summary>
/// <remarks>
/// The one place that knows what each <see cref="ThresholdSubject"/> means. A subject that
/// does not apply to the scope it was asked for returns null rather than zero, so a
/// mismatched rule is a skipped check instead of a check that silently passes against a
/// number that was never measured.
/// </remarks>
internal static class ThresholdSubjectReader
{
    public static double? Read(Threshold threshold, ScenarioStats scnStats, IReadOnlyList<MetricStats> metrics) =>
        threshold.Scope switch
        {
            ThresholdScope.Scenario => FromMeasurement(
                threshold.Subject, scnStats.Ok, scnStats.Fail, scnStats.AllRequestCount),

            ThresholdScope.Step => ReadStep(threshold, scnStats),
            ThresholdScope.StatusCode => ReadStatusCode(threshold, scnStats),
            ThresholdScope.Metric => ReadMetric(threshold, metrics),

            _ => null
        };

    private static double? ReadStep(Threshold threshold, ScenarioStats scnStats)
    {
        var step = scnStats.StepStats.FirstOrDefault(x => x.StepName == threshold.StepName);
        if (step is null) return null;

        var allRequests = step.Ok.Request.Count + step.Fail.Request.Count;
        return FromMeasurement(threshold.Subject, step.Ok, step.Fail, allRequests);
    }

    private static double? FromMeasurement(
        ThresholdSubject subject, MeasurementStats ok, MeasurementStats fail, int allRequests)
    {
        var okCount = ok.Request.Count;
        var failCount = fail.Request.Count;

        return subject switch
        {
            // A window with no requests has no error rate. Reporting 0 would let a rule about
            // reliability pass on a scenario that did nothing at all.
            ThresholdSubject.ErrorRate => allRequests == 0 ? null : (double)failCount / allRequests,
            ThresholdSubject.OkRate => allRequests == 0 ? null : (double)okCount / allRequests,

            ThresholdSubject.RequestCount => allRequests,
            ThresholdSubject.OkCount => okCount,
            ThresholdSubject.FailCount => failCount,
            ThresholdSubject.Rps => ok.Request.RPS,

            ThresholdSubject.MinLatency => ok.Latency.MinMs,
            ThresholdSubject.MeanLatency => ok.Latency.MeanMs,
            ThresholdSubject.MaxLatency => ok.Latency.MaxMs,
            ThresholdSubject.Percent50 => ok.Latency.Percent50,
            ThresholdSubject.Percent75 => ok.Latency.Percent75,
            ThresholdSubject.Percent95 => ok.Latency.Percent95,
            ThresholdSubject.Percent99 => ok.Latency.Percent99,

            ThresholdSubject.AllBytes => ok.DataTransfer.AllBytes,

            _ => null
        };
    }

    private static double? ReadStatusCode(Threshold threshold, ScenarioStats scnStats)
    {
        var count = scnStats.Ok.StatusCodes.Where(x => x.StatusCode == threshold.StatusCode).Sum(x => x.Count)
                    + scnStats.Fail.StatusCodes.Where(x => x.StatusCode == threshold.StatusCode).Sum(x => x.Count);

        return threshold.Subject switch
        {
            // A code that never came back counts zero rather than nothing: "fewer than ten
            // 500s" is satisfied by no 500s at all.
            ThresholdSubject.StatusCodeCount => count,
            ThresholdSubject.StatusCodeRate =>
                scnStats.AllRequestCount == 0 ? null : (double)count / scnStats.AllRequestCount,
            _ => null
        };
    }

    private static double? ReadMetric(Threshold threshold, IReadOnlyList<MetricStats> metrics)
    {
        var metric = metrics.FirstOrDefault(x => x.Name == threshold.MetricName);
        if (metric is null) return null;

        return threshold.Subject switch
        {
            ThresholdSubject.MetricCurrent => metric.Current,
            ThresholdSubject.MetricMin => metric.Min,
            ThresholdSubject.MetricMean => metric.Mean,
            ThresholdSubject.MetricMax => metric.Max,
            ThresholdSubject.MetricPercent50 => metric.Percent50,
            ThresholdSubject.MetricPercent95 => metric.Percent95,
            ThresholdSubject.MetricPercent99 => metric.Percent99,
            ThresholdSubject.MetricCount => metric.Count,
            _ => null
        };
    }
}
