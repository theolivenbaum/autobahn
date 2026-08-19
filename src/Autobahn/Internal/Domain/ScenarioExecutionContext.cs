using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Autobahn.Internal.Domain.Stats;
using Autobahn.Stats;

namespace Autobahn.Internal.Domain;

/// <summary>What one scenario copy sees while it runs. The engine side of <see cref="IScenarioContext"/>.</summary>
internal sealed class ScenarioExecutionContext(
    ScenarioContextArgs args,
    Stopwatch timer,
    ScenarioInfo scenarioInfo) : IScenarioContext
{
    private readonly Dictionary<string, object> _data = [];
    private int _invocationNumber;

    public bool RestartIterationOnFail { get; } = args.Scenario.RestartIterationOnFail;
    public ScenarioStatsActor StatsActor { get; } = args.ScenarioStatsActor;
    public Stopwatch Timer { get; } = timer;

    public TimeSpan CurrentTimeBucket => args.CurrentTimeBucket;

    public void PrepareNextIteration()
    {
        _invocationNumber++;
        _data.Clear();
    }

    public TestInfo TestInfo => args.TestInfo;
    public ScenarioInfo ScenarioInfo => scenarioInfo;
    public HostInfo HostInfo => args.GetHostInfo();
    public ILogger Logger => args.Logger;
    public int InvocationNumber => _invocationNumber;
    public Dictionary<string, object> Data => _data;

    public void StopCurrentTest(string reason) =>
        args.ExecStopCommand(new StopCommand.StopTest(reason));

    public void StopScenario(string scenarioName, string reason) =>
        args.ExecStopCommand(new StopCommand.StopScenario(scenarioName, reason));
}
