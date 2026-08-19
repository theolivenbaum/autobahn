using System.Diagnostics;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Metrics;
using Autobahn.Stats;
using Microsoft.Extensions.Logging;

namespace Autobahn.Internal.Domain;

/// <summary>What one scenario copy sees while it runs. The engine side of <see cref="IScenarioContext"/>.</summary>
internal sealed class ScenarioExecutionContext : IScenarioContext, IDisposable
{
    private readonly ScenarioContextArgs _args;
    private readonly ScenarioInfo _scenarioInfo;
    private readonly Dictionary<string, object> _data = [];
    private readonly CancellationToken _scenarioToken;
    private readonly TimeSpan? _iterationTimeout;

    private CancellationTokenSource? _iterationCts;
    private int _invocationNumber;

    public ScenarioExecutionContext(ScenarioContextArgs args, Stopwatch timer, ScenarioInfo scenarioInfo)
    {
        _args = args;
        _scenarioInfo = scenarioInfo;
        _scenarioToken = args.ScenarioCancellationToken.Token;
        _iterationTimeout = args.Scenario.IterationTimeout;

        Timer = timer;
        RestartIterationOnFail = args.Scenario.RestartIterationOnFail;
        StatsActor = args.ScenarioStatsActor;
    }

    public bool RestartIterationOnFail { get; }
    public ScenarioStatsActor StatsActor { get; }
    public Stopwatch Timer { get; }

    public TimeSpan CurrentTimeBucket => _args.CurrentTimeBucket;

    /// <summary>The timeout this scenario applies to one iteration, or null when it applies none.</summary>
    public TimeSpan? IterationTimeout => _iterationTimeout;

    public void PrepareNextIteration()
    {
        _invocationNumber++;
        _data.Clear();

        _iterationCts?.Dispose();

        // A per-iteration source only exists when there is a timeout to enforce; otherwise
        // user code gets the scenario's own token and there is nothing extra to allocate.
        _iterationCts = _iterationTimeout is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(_scenarioToken)
            : null;
    }

    /// <summary>Signals the current iteration to stop, after it outran its timeout.</summary>
    public void CancelIteration()
    {
        try
        {
            _iterationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The iteration finished between the timeout firing and this call.
        }
    }

    public TestInfo TestInfo => _args.TestInfo;
    public ScenarioInfo ScenarioInfo => _scenarioInfo;
    public HostInfo HostInfo => _args.GetHostInfo();
    public ILogger Logger => _args.Logger;
    public IMetricRegistry Metrics => _args.Metrics;
    public int InvocationNumber => _invocationNumber;
    public Dictionary<string, object> Data => _data;
    public CancellationToken CancellationToken => _iterationCts?.Token ?? _scenarioToken;

    public void StopCurrentTest(string reason) =>
        _args.ExecStopCommand(new StopCommand.StopTest(reason));

    public void StopScenario(string scenarioName, string reason) =>
        _args.ExecStopCommand(new StopCommand.StopScenario(scenarioName, reason));

    public void Dispose() => _iterationCts?.Dispose();
}
