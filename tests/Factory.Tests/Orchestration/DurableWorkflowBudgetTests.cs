using System.Security.Cryptography;
using Factory.Core.Models;
using Factory.Core.Orchestration;
using Factory.Orchestration.Durable;
using Factory.Tests.Lifecycle;
using Npgsql;

namespace Factory.Tests.Orchestration;

[Collection("Lifecycle database")]
public class DurableWorkflowBudgetTests
{
    private static string Provider() => "test-" + Guid.NewGuid().ToString("N");

    // $1 per million input and output tokens: one micro-USD per token keeps the arithmetic exact.
    private static Task Price(string provider) => Durable.Sql("""
        INSERT INTO orchestration."ModelPrices" ("Provider","Model","EffectiveFrom","InputMicrosPerMillion","OutputMicrosPerMillion","Source")
        VALUES (@p,'m',now()-interval '1 hour',1000000,1000000,'synthetic test price')
        """, new NpgsqlParameter("p", provider));

    private static Task Budget(string scope, string id, long? limit, string period = "Month") => Durable.Sql("""
        INSERT INTO orchestration."Budgets" ("Id","Scope","ScopeId","Period","LimitMicros","EffectiveFrom","Source")
        VALUES (gen_random_uuid(),@s,@i,@period,@l,clock_timestamp()-interval '1 second','synthetic test budget')
        """, new NpgsqlParameter("s", scope), new NpgsqlParameter("i", id), new NpgsqlParameter("period", period),
        new NpgsqlParameter("l", (object?)limit ?? DBNull.Value));

    private static DurableWorkflowProcessor Processor(string provider) => new(Durable.Worker, new Durable.Provider
    {
        AnalysisUsage = new ModelUsage(provider, "m", 1000, 200), ArchitectureUsage = new ModelUsage(provider, "m", 2000, 400)
    }, new ModelIdentity(provider, "m"));

    [OrchestrationFact]
    public async Task ExceededBudgetPausesForTheCustomerAndResumesOnlyAfterTheyDecide()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var provider = Provider(); await Price(provider); await Budget("Project", f.Project, 1000);
        var run = await Durable.Submit(f);
        Assert.Equal(StepOutcome.Suspended, await Processor(provider).ProcessNextAsync("worker", default));
        var waiting = await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId);
        Assert.Equal((RunStatus.Waiting, "budget_exceeded", 0), (waiting.Status, waiting.Decisions![0].Reason, waiting.Usage!.Calls));
        // Nothing continues automatically, even after the budget is raised.
        await Budget("Project", f.Project, 10_000_000);
        Assert.Equal(StepOutcome.Idle, await Processor(provider).ProcessNextAsync("worker", default));
        await Durable.Api.DecideAsync(f.Actor, f.Project, run.RunId, waiting.Decisions[0].Id, "continue");
        for (var i = 0; i < 3; i++) Assert.Equal(StepOutcome.Completed, await Processor(provider).ProcessNextAsync("worker", default));
        var done = await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId);
        // Settled calls count at their actual estimate: 1000+200 and 2000+400 micro-USD.
        Assert.Equal((RunStatus.Succeeded, 3600L), (done.Status, done.Usage!.EstimatedCostMicros));
        var project = Assert.Single(await Durable.Api.GetBudgetsAsync(f.Actor, f.Organization), b => b.Scope == BudgetScope.Project);
        Assert.Equal((10_000_000L, 3600L, 10_000_000L - 3600L), (project.LimitMicros, project.SpentMicros, project.RemainingMicros));
    }

    [OrchestrationFact]
    public async Task UnpricedModelsCannotRunUnderABudgetButRunFreelyWithoutOne()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); await using var free = await LifecycleFixture.Create();
        var provider = Provider(); await Budget("Organization", f.Organization, 1_000_000_000);
        await Durable.Submit(f);
        Assert.Equal(StepOutcome.Suspended, await Processor(provider).ProcessNextAsync("worker", default));
        await Durable.Drain();
        var run = await Durable.Submit(free);
        Assert.Equal(StepOutcome.Completed, await Processor(provider).ProcessNextAsync("worker", default));
        var view = await Durable.Api.GetRunAsync(free.Actor, free.Project, run.RunId);
        Assert.Equal((1, 1), (view.Usage!.Calls, view.Usage.UnpricedCalls));
        Assert.Equal(1L, await Durable.Scalar("""SELECT count(*) FROM orchestration."CostReservations" WHERE "RunId"=@r AND "ReservedMicros" IS NULL""",
            new NpgsqlParameter("r", run.RunId)));
        await Durable.Drain();
    }

    [OrchestrationFact]
    public async Task ProviderLifetimeCapAppliesAcrossTenants()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        var provider = Provider(); await Price(provider); await Budget("Provider", provider, 20_000, "Lifetime");
        await Durable.Submit(f); await Durable.Submit(other);
        var tokens = new[] { RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32) };
        var mine = (await Durable.Worker.ClaimAsync("a", tokens[0]))!; var theirs = (await Durable.Worker.ClaimAsync("b", tokens[1]))!;
        Assert.Equal((f.Project, other.Project), (mine.Request.ProjectId, theirs.Request.ProjectId));
        // 1000 + 16000 = 17,000 micro-USD each; the second tenant's reservation would exceed the cap.
        await Durable.Worker.ReserveCostAsync(mine.AttemptId, tokens[0], new ModelIdentity(provider, "m"), 1000, 16_000);
        Assert.Equal(OrchestrationError.BudgetExceeded, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Worker.ReserveCostAsync(theirs.AttemptId, tokens[1], new ModelIdentity(provider, "m"), 1000, 16_000))).Error);
        // Another provider is unaffected by this cap.
        await Durable.Worker.ReserveCostAsync(theirs.AttemptId, tokens[1], new ModelIdentity(Provider(), "m"), 1000, 16_000);
        await Durable.Drain();
    }

    [OrchestrationFact]
    public async Task UnsettledReservationsCountAtWorstCaseAndConcurrentReservationsCannotOverspend()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var provider = Provider(); await Price(provider); await Budget("Project", f.Project, 30_000);
        await Durable.Submit(f); await Durable.Submit(f);
        var tokens = new[] { RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32) };
        var steps = new[] { (await Durable.Worker.ClaimAsync("a", tokens[0]))!, (await Durable.Worker.ClaimAsync("b", tokens[1]))! };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<object> Reserve(int i)
        {
            await gate.Task;
            try { return await Durable.Worker.ReserveCostAsync(steps[i].AttemptId, tokens[i], new ModelIdentity(provider, "m"), 1000, 16_000); }
            catch (OrchestrationException ex) { return ex.Error; }
        }
        var racing = new[] { Reserve(0), Reserve(1) }; gate.SetResult();
        var results = await Task.WhenAll(racing);
        var reserved = Assert.Single(results.OfType<Guid>()); Assert.Equal(OrchestrationError.BudgetExceeded, Assert.Single(results.OfType<OrchestrationError>()));
        // A crashed call never settles: it stays counted at its 17,000 micro-USD worst case.
        Assert.Equal(17_000L, Assert.Single(await Durable.Api.GetBudgetsAsync(f.Actor, f.Organization)).SpentMicros);
        var owner = Array.FindIndex(results, r => r is Guid);
        await Durable.Worker.RecordModelCallAsync(steps[owner].AttemptId, tokens[owner],
            new ModelCall(provider, "m-2026-01-01", 1000, 50, 5, ModelCallOutcome.Completed, reserved));
        Assert.Equal(1050L, Assert.Single(await Durable.Api.GetBudgetsAsync(f.Actor, f.Organization)).SpentMicros);
        // A reservation settles once, only for its own attempt and provider.
        var other = 1 - owner;
        foreach (var call in new Func<Task>[] {
            () => Durable.Worker.RecordModelCallAsync(steps[owner].AttemptId, tokens[owner], new ModelCall(provider, "m", 1, 1, 1, ModelCallOutcome.Completed, reserved)),
            () => Durable.Worker.RecordModelCallAsync(steps[other].AttemptId, tokens[other], new ModelCall(provider, "m", 1, 1, 1, ModelCallOutcome.Completed, reserved)) })
            Assert.Equal(OrchestrationError.InvalidCommand, (await Assert.ThrowsAsync<OrchestrationException>(call)).Error);
        await Durable.Drain();
    }

    [OrchestrationFact]
    public async Task BudgetsAreVisibleOnlyToTheirOrganizationAdminsAndRolesAreSeparated()
    {
        await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        var provider = Provider();
        await Budget("Organization", f.Organization, 5_000_000); await Budget("Project", f.Project, 1_000_000);
        await Budget("Provider", provider, 1, "Lifetime");
        var budgets = await Durable.Api.GetBudgetsAsync(f.Actor, f.Organization);
        Assert.Equal([BudgetScope.Organization, BudgetScope.Project], budgets.Select(b => b.Scope));
        Assert.All(budgets, b => Assert.Equal((BudgetPeriod.Month, 0L), (b.Period, b.SpentMicros)));
        Assert.Equal(OrchestrationError.Forbidden, (await Assert.ThrowsAsync<OrchestrationException>(() => Durable.Api.GetBudgetsAsync(other.Actor, f.Organization))).Error);
        // Lifting a budget is a new row with no limit; history is never rewritten.
        await Budget("Project", f.Project, null);
        Assert.Equal([BudgetScope.Organization], (await Durable.Api.GetBudgetsAsync(f.Actor, f.Organization)).Select(b => b.Scope));
        await Assert.ThrowsAsync<PostgresException>(() => Durable.Sql("""UPDATE orchestration."Budgets" SET "LimitMicros"=0"""));
        await Assert.ThrowsAsync<PostgresException>(() => Durable.Sql("""DELETE FROM orchestration."CostReservations" """));

        await using var runtime = new NpgsqlConnection(LifecycleFixture.Runtime); await runtime.OpenAsync();
        foreach (var sql in new[] { "SELECT orchestration.reserve_cost(gen_random_uuid(),'\\x00'::bytea,'p','m',1,1)", """SELECT * FROM orchestration."Budgets" """ })
        {
            await using var command = new NpgsqlCommand(sql, runtime);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
        await using var worker = new NpgsqlConnection(Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")); await worker.OpenAsync();
        foreach (var sql in new[] { "SELECT tenancy.read_budgets('o','i','s','Human')", """SELECT * FROM orchestration."CostReservations" """,
            """INSERT INTO orchestration."Budgets" VALUES (gen_random_uuid(),'Provider','x','Month',NULL,now(),'x')""" })
        {
            await using var command = new NpgsqlCommand(sql, worker);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
    }
}
