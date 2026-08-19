using Autobahn.Internal.Domain.Concurrency;

namespace Autobahn.Internal.Domain.Scheduler;

/// <summary>
/// The open-model scheduler: every interval it starts N actors for exactly one iteration,
/// renting from the pool and growing it when there are not enough free actors.
/// </summary>
internal sealed class OneTimeActorScheduler(ScenarioContextArgs scnCtx) : IDisposable
{
    private readonly List<ScenarioActor> _actorPool = [];
    private int _scheduledActorCount;

    public int ScheduledActorCount => _scheduledActorCount;

    public IReadOnlyList<ScenarioActor> AvailableActors => _actorPool;

    public void InjectActors(int count, TimeSpan injectInterval)
    {
        _scheduledActorCount = count;
        Exec(CreateActors, _actorPool, _scheduledActorCount, injectInterval);
    }

    public void AskToStop() => ScenarioActorPool.AskToStop(_actorPool);

    public void Dispose() => AskToStop();

    private ScenarioActor[] CreateActors(int count, int fromIndex) =>
        ScenarioActorPool.CreateActors(scnCtx, count, fromIndex);

    internal static void Exec(
        Func<int, int, ScenarioActor[]> createActors,
        List<ScenarioActor> actorPool,
        int scheduledActorCount,
        TimeSpan injectInterval)
    {
        var freeActors = actorPool.Where(x => !x.Working).Take(scheduledActorCount).ToArray();

        if (freeActors.Length >= scheduledActorCount)
        {
            ExecSteps(freeActors, injectInterval);
            return;
        }

        var result = ScenarioActorPool.RentActors(createActors, actorPool, scheduledActorCount);

        ExecSteps(result.ActorsFromPool, injectInterval);
        ExecSteps(result.NewActors, injectInterval);

        actorPool.AddRange(result.NewActors);
    }

    private static void ExecSteps(ScenarioActor[] actors, TimeSpan injectInterval)
    {
        foreach (var actor in actors) _ = actor.ExecSteps(injectInterval);
    }
}
