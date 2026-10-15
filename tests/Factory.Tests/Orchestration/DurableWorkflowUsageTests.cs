using System.Security.Cryptography;
using Factory.Core.Models;
using Factory.Core.Orchestration;
using Factory.Orchestration.Durable;
using Factory.Tests.Lifecycle;
using Npgsql;

namespace Factory.Tests.Orchestration;

[Collection("Lifecycle database")]
public class DurableWorkflowUsageTests
{
    // Synthetic provider names keep the global, append-only price table free of real-looking prices.
    private static string Provider() => "test-" + Guid.NewGuid().ToString("N");

    private static Task Price(string provider, string model, long input, long output, string offset = "-1 hour")
        => Durable.Sql($"""
            INSERT INTO orchestration."ModelPrices" ("Provider","Model","EffectiveFrom","InputMicrosPerMillion","OutputMicrosPerMillion","Source")
            VALUES (@p,@m,now()+interval '{offset}',@i,@o,'synthetic test price')
            """, new NpgsqlParameter("p", provider), new NpgsqlParameter("m", model), new NpgsqlParameter("i", input), new NpgsqlParameter("o", output));

    [OrchestrationFact]
    public async Task UsageIsPricedAtCallTimeAndSummedPerRunAndProject()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var provider = Provider();
        await Price(provider, "m", 150_000, 600_000);
        var run = await Durable.Submit(f);
        var models = new Durable.Provider
        {
            // Dated snapshot names match the base price row.
            AnalysisUsage = new ModelUsage(provider, "m-2026-01-01", 1000, 200),
            ArchitectureUsage = new ModelUsage(provider, "m-2026-01-01", 2000, 400)
        };
        var processor = new DurableWorkflowProcessor(Durable.Worker, models);
        for (var i = 0; i < 3; i++) await processor.ProcessNextAsync("worker", default);
        // ceil((1000*0.15 + 200*0.60)) = 270 and ceil((2000*0.15 + 400*0.60)) = 540 micro-USD.
        var expected = new UsageTotals(2, 3000, 600, 810, 0, 0);
        Assert.Equal(expected, (await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId)).Usage);
        await Price(provider, "m", 9_000_000, 9_000_000, "-1 second");
        Assert.Equal(expected, (await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId)).Usage);
        var usage = await Durable.Api.GetUsageAsync(f.Actor, f.Project, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(expected, usage.Totals);
        var model = Assert.Single(usage.Models);
        Assert.Equal((provider, "m-2026-01-01", expected), (model.Provider, model.Model, model.Totals));
    }

    [OrchestrationFact]
    public async Task UnpricedAndUnknownUsageAreReportedSeparatelyNeverAsZeroCost()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var run = await Durable.Submit(f);
        var models = new Durable.Provider { AnalysisUsage = new ModelUsage(Provider(), "unpriced-model", 500, 50), FailArchitecture = true };
        var processor = new DurableWorkflowProcessor(Durable.Worker, models);
        await processor.ProcessNextAsync("worker", default);
        Assert.Equal(StepOutcome.Failed, await processor.ProcessNextAsync("worker", default));
        var view = await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId);
        Assert.Equal((RunStatus.Failed, "generation_failed"), (view.Status, view.FailureCode));
        Assert.Equal(new UsageTotals(2, 500, 50, 0, 1, 1), view.Usage);
    }

    [OrchestrationFact]
    public async Task RecordingRequiresOwnershipValidInputAndIsBoundedPerAttempt()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); var run = await Durable.Submit(f);
        var token = RandomNumberGenerator.GetBytes(32); var step = (await Durable.Worker.ClaimAsync("worker", token))!;
        var call = new ModelCall("p", "m", 1, 1, 10, ModelCallOutcome.Completed);
        async Task<OrchestrationError> Error(Func<Task> action) => (await Assert.ThrowsAsync<OrchestrationException>(action)).Error;
        Assert.Equal(OrchestrationError.Forbidden, await Error(() => Durable.Worker.RecordModelCallAsync(step.AttemptId, RandomNumberGenerator.GetBytes(32), call)));
        Assert.Equal(OrchestrationError.NotFound, await Error(() => Durable.Worker.RecordModelCallAsync(Guid.NewGuid(), token, call)));
        Assert.Equal(OrchestrationError.InvalidCommand, await Error(() => Durable.Worker.RecordModelCallAsync(step.AttemptId, token, call with { Provider = null })));
        Assert.Equal(OrchestrationError.InvalidCommand, await Error(() => Durable.Worker.RecordModelCallAsync(step.AttemptId, token, call with { OutputTokens = null })));
        Assert.Equal(OrchestrationError.InvalidCommand, await Error(() => Durable.Worker.RecordModelCallAsync(step.AttemptId, token, call with { Model = "bad model; --" })));
        // A call that returns after the lease was lost is still recorded: it was made.
        await Durable.ExpireLeases(run.RunId);
        Assert.NotNull(await Durable.Worker.ClaimAsync("other", RandomNumberGenerator.GetBytes(32)));
        for (var i = 0; i < 10; i++) await Durable.Worker.RecordModelCallAsync(step.AttemptId, token, call);
        Assert.Equal(OrchestrationError.Capacity, await Error(() => Durable.Worker.RecordModelCallAsync(step.AttemptId, token, call)));
        Assert.Equal(10, (await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId)).Usage!.Calls);
        await Durable.Drain();
    }

    [OrchestrationFact]
    public async Task UsageReadsAreAuthorizedBoundedAndRolesSeparated()
    {
        await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(OrchestrationError.Forbidden, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.GetUsageAsync(other.Actor, f.Project, now.AddDays(-1), now))).Error);
        Assert.Equal(OrchestrationError.InvalidCommand, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.GetUsageAsync(f.Actor, f.Project, now, now))).Error);
        Assert.Equal(OrchestrationError.InvalidCommand, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.GetUsageAsync(f.Actor, f.Project, now.AddDays(-400), now))).Error);
        var empty = await Durable.Api.GetUsageAsync(f.Actor, f.Project, now.AddDays(-1), now);
        Assert.Equal((new UsageTotals(0, 0, 0, 0, 0, 0), 0), (empty.Totals, empty.Models.Count));

        await using var worker = new NpgsqlConnection(Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")); await worker.OpenAsync();
        foreach (var sql in new[] { """SELECT * FROM orchestration."ModelCalls" """, """SELECT * FROM orchestration."ModelPrices" """,
            """INSERT INTO orchestration."ModelPrices" VALUES ('p','m',now(),0,0,'x')""",
            "SELECT orchestration.read_usage('p','i','s','Human',now()-interval '1 day',now())" })
        {
            await using var command = new NpgsqlCommand(sql, worker);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
        await using var runtime = new NpgsqlConnection(LifecycleFixture.Runtime); await runtime.OpenAsync();
        foreach (var sql in new[] { "SELECT orchestration.record_model_call(gen_random_uuid(),'\\x00'::bytea,'p','m',1,1,1,'Completed',NULL::uuid)",
            """UPDATE orchestration."ModelCalls" SET "InputTokens"=0""" })
        {
            await using var command = new NpgsqlCommand(sql, runtime);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
        // Prices and calls are append-only even for the owner.
        await Assert.ThrowsAsync<PostgresException>(() => Durable.Sql("""UPDATE orchestration."ModelPrices" SET "InputMicrosPerMillion"=0"""));
        await Assert.ThrowsAsync<PostgresException>(() => Durable.Sql("""DELETE FROM orchestration."ModelCalls" """));
    }
}
