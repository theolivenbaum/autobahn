namespace Autobahn.Internal.Domain.Concurrency;

/// <summary>What renting actors produced: the ones reused from the pool, and the ones just created.</summary>
internal readonly record struct ActorPoolResult(ScenarioActor[] ActorsFromPool, ScenarioActor[] NewActors);

/// <summary>Creating, renting and stopping the actors that back one scenario.</summary>
internal static class ScenarioActorPool
{
    public static ScenarioActor[] CreateActors(ScenarioContextArgs scnCtx, int count, int fromIndex)
    {
        var scenario = scnCtx.Scenario;
        var actors = new ScenarioActor[count];

        for (var i = 0; i < count; i++)
        {
            var actorIndex = fromIndex + i;

            var scenarioInfo = ScenarioFactory.CreateScenarioInfo(
                scenario.ScenarioName, scenario.PlanedDuration, actorIndex, scnCtx.ScenarioOperation);

            actors[i] = new ScenarioActor(scnCtx, scenarioInfo);
        }

        return actors;
    }

    /// <summary>
    /// Takes up to <paramref name="actorCount"/> free actors from the pool, creating the shortfall.
    /// </summary>
    public static ActorPoolResult RentActors(
        Func<int, int, ScenarioActor[]> createActors, List<ScenarioActor> actorPool, int actorCount)
    {
        var notWorkingActors = actorPool.Where(x => !x.Working).Take(actorCount).ToArray();

        if (notWorkingActors.Length >= actorCount)
            return new ActorPoolResult(notWorkingActors, []);

        var createCount = actorCount - notWorkingActors.Length;
        var newActors = createActors(createCount, actorPool.Count);

        return new ActorPoolResult(notWorkingActors, newActors);
    }

    public static void AskToStop(IEnumerable<ScenarioActor> actorPool)
    {
        foreach (var actor in actorPool) actor.AskToStop();
    }

    public static IEnumerable<ScenarioActor> GetWorkingActors(IEnumerable<ScenarioActor> actorPool) =>
        actorPool.Where(x => x.Working);
}
