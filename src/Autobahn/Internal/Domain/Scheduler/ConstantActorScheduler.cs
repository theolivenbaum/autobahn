using Autobahn.Internal.Domain.Concurrency;

namespace Autobahn.Internal.Domain.Scheduler;

/// <summary>What the constant scheduler decided to do this interval.</summary>
internal enum ConstantSchedulerCommand
{
    KeepWorking,
    AddActors,
    RemoveActors,
    StopScheduler
}

/// <summary>
/// The closed-model scheduler: keeps a pool of long-lived actors at a target count,
/// adding and removing to match.
/// </summary>
internal sealed class ConstantActorScheduler(ScenarioContextArgs scnCtx) : IDisposable
{
    private readonly List<ScenarioActor> _actorPool = [];
    private int _scheduledActorCount;

    public int ScheduledActorCount => _scheduledActorCount;

    /// <summary>Every actor this scheduler has ever created, working or idle.</summary>
    public IReadOnlyList<ScenarioActor> AvailableActors => _actorPool;

    public void AddActors(int count, TimeSpan injectInterval)
    {
        _scheduledActorCount += count;
        Exec(CreateActors, _actorPool, _scheduledActorCount, injectInterval);
    }

    public void RemoveActors(int count)
    {
        _scheduledActorCount = RemoveFromScheduler(_scheduledActorCount, count);
        Exec(CreateActors, _actorPool, _scheduledActorCount, TimeSpan.Zero);
    }

    public void AskToStop() => ScenarioActorPool.AskToStop(_actorPool);

    public void Dispose()
    {
        AskToStop();
        foreach (var actor in _actorPool) actor.Dispose();
    }

    private ScenarioActor[] CreateActors(int count, int fromIndex) =>
        ScenarioActorPool.CreateActors(scnCtx, count, fromIndex);

    internal static int RemoveFromScheduler(int scheduledActorsCount, int removeCount)
    {
        var actorsCount = scheduledActorsCount - removeCount;
        return actorsCount < 0 ? 0 : actorsCount;
    }

    internal static (ConstantSchedulerCommand Command, int Count) Schedule(int workingActorCount, int scheduledActorCount)
    {
        if (scheduledActorCount == 0) return (ConstantSchedulerCommand.StopScheduler, 0);
        if (workingActorCount == scheduledActorCount) return (ConstantSchedulerCommand.KeepWorking, 0);

        return workingActorCount < scheduledActorCount
            ? (ConstantSchedulerCommand.AddActors, scheduledActorCount - workingActorCount)
            : (ConstantSchedulerCommand.RemoveActors, workingActorCount - scheduledActorCount);
    }

    internal static void Exec(
        Func<int, int, ScenarioActor[]> createActors,
        List<ScenarioActor> actorPool,
        int scheduledActorCount,
        TimeSpan injectInterval)
    {
        var workingActors = ScenarioActorPool.GetWorkingActors(actorPool).ToArray();
        var (command, count) = Schedule(workingActors.Length, scheduledActorCount);

        switch (command)
        {
            case ConstantSchedulerCommand.KeepWorking:
                break;

            case ConstantSchedulerCommand.AddActors:
            {
                var result = ScenarioActorPool.RentActors(createActors, actorPool, count);

                foreach (var actor in result.ActorsFromPool) _ = actor.RunInfinite(injectInterval);
                foreach (var actor in result.NewActors) _ = actor.RunInfinite(injectInterval);

                actorPool.AddRange(result.NewActors);
                break;
            }

            case ConstantSchedulerCommand.RemoveActors:
                foreach (var actor in workingActors.Take(count)) actor.AskToStop();
                break;

            case ConstantSchedulerCommand.StopScheduler:
                ScenarioActorPool.AskToStop(actorPool);
                break;
        }
    }
}
