using System.Security.Cryptography;
using Factory.Core.Orchestration;
using Factory.Orchestration.Durable;
using Factory.Tests.Lifecycle;
using Npgsql;

namespace Factory.Tests.Orchestration;

[Collection("Lifecycle database")]
public class ModelRoutingTests
{
    [OrchestrationFact]
    public async Task EveryModelAttemptRecordsItsRouteAndTheRunViewShowsIt()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var run = await Durable.Submit(f);
        var standard = new Durable.Provider(); var strong = new Durable.Provider();
        var catalog = new ModelCatalog(new CatalogEntry(new Durable.Provider(), null), new Dictionary<ModelTier, CatalogEntry>
        {
            [ModelTier.Standard] = new(standard, new ModelIdentity("test-standard", "standard-model")),
            [ModelTier.Strong] = new(strong, new ModelIdentity("test-strong", "strong-model"))
        });
        var processor = new DurableWorkflowProcessor(Durable.Worker, new RegistryModelResolver(Durable.Worker, catalog, null));
        for (var i = 0; i < 3; i++) Assert.Equal(StepOutcome.Completed, await processor.ProcessNextAsync("worker", default));
        var view = await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId);
        Assert.Equal(RunStatus.Succeeded, view.Status);
        Assert.Equal((1, 0, 1, 0), (standard.Analysis, standard.Architecture, strong.Architecture, strong.Analysis));
        // Publication makes no model call and has no route.
        Assert.Equal(
        [
            new StepRoute(0, 1, ModelTier.Standard, "role_minimum", "routing-v1", "test-standard", "standard-model"),
            new StepRoute(1, 1, ModelTier.Strong, "role_minimum", "routing-v1", "test-strong", "strong-model")
        ], view.Routes);
    }

    [OrchestrationFact]
    public async Task RoutesAreLeaseCheckedValidatedWrittenOnceAndHiddenFromRuntimeRoles()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        await Durable.Submit(f);
        var token = RandomNumberGenerator.GetBytes(32); var step = (await Durable.Worker.ClaimAsync("worker", token))!;
        var route = ModelRoutingPolicy.Route(StepKind.Analysis);
        async Task<OrchestrationError> Error(Func<Task> action) => (await Assert.ThrowsAsync<OrchestrationException>(action)).Error;
        Assert.Equal(OrchestrationError.Forbidden, await Error(() =>
            Durable.Worker.RecordRouteAsync(step.AttemptId, RandomNumberGenerator.GetBytes(32), route, null)));
        Assert.Equal(OrchestrationError.InvalidCommand, await Error(() =>
            Durable.Worker.RecordRouteAsync(step.AttemptId, token, route with { Reason = "Not A Code" }, null)));
        Assert.Equal(OrchestrationError.InvalidCommand, await Error(() =>
            Durable.Worker.RecordRouteAsync(step.AttemptId, token, route with { Tier = (ModelTier)9 }, null)));
        await Durable.Worker.RecordRouteAsync(step.AttemptId, token, route, new ModelIdentity("openai", "gpt-4o-mini"));
        Assert.Equal(OrchestrationError.Conflict, await Error(() => Durable.Worker.RecordRouteAsync(step.AttemptId, token, route, null)));
        // The owner login cannot rewrite evidence either.
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() =>
            Durable.Sql("""UPDATE orchestration."StepRoutes" SET "Tier"='Strong' WHERE "AttemptId"=@a""", new NpgsqlParameter("a", step.AttemptId)))).SqlState);
        // After the lease ends, no route can be added for the attempt.
        await Durable.ExpireLeases(step.RunId);
        Assert.Equal(OrchestrationError.LeaseLost, await Error(() => Durable.Worker.RecordRouteAsync(step.AttemptId, token, route, null)));
        foreach (var connection in new[] { LifecycleFixture.Runtime, Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")! })
        {
            await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
            await using var command = new NpgsqlCommand("""SELECT * FROM orchestration."StepRoutes" """, db);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
        await Durable.Drain();
    }
}
