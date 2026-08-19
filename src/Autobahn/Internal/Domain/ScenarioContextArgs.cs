using Autobahn.Internal.Domain.Stats;
using Autobahn.Metrics;
using Autobahn.Stats;
using Microsoft.Extensions.Logging;

namespace Autobahn.Internal.Domain;

/// <summary>
/// Everything one scenario's actors and schedulers share: the scenario itself, where to
/// send measurements, how to stop, and which reporting interval is currently open.
/// </summary>
internal sealed class ScenarioContextArgs
{
    public required ILogger Logger { get; init; }
    public required RuntimeScenario Scenario { get; init; }
    public required CancellationTokenSource ScenarioCancellationToken { get; init; }
    public required ScenarioOperation ScenarioOperation { get; init; }
    public required ScenarioStatsActor ScenarioStatsActor { get; init; }
    public required Action<StopCommand> ExecStopCommand { get; init; }
    public required TestInfo TestInfo { get; init; }
    public required Func<HostInfo> GetHostInfo { get; init; }
    public required IMetricRegistry Metrics { get; init; }

    /// <summary>
    /// The remaining allowance of the counted simulation currently running, or null while a
    /// timed one is. Set by the scheduler as it moves between segments; read by every actor.
    /// </summary>
    public IterationBudget? IterationBudget { get; set; }

    private long _currentTimeBucketTicks;

    /// <summary>
    /// The reporting interval measurements started right now belong to. Advanced by the
    /// scenario scheduler and read by every actor, hence the volatile access.
    /// </summary>
    public TimeSpan CurrentTimeBucket
    {
        get => new(Volatile.Read(ref _currentTimeBucketTicks));
        set => Volatile.Write(ref _currentTimeBucketTicks, value.Ticks);
    }
}
