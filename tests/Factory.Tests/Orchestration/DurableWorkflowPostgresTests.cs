using System.Security.Cryptography;
using System.Text.Json;
using Factory.Core.Analysis;
using Factory.Core.Architecture;
using Factory.Core.Lifecycle;
using Factory.Core.Models;
using Factory.Core.Orchestration;
using Factory.Core.Reviews;
using Factory.Core.Workflows;
using Factory.Infrastructure.Orchestration;
using Factory.Infrastructure.Reviews;
using Factory.Orchestration.Durable;
using Factory.Tests.Lifecycle;
using Factory.Tests.Reviews;
using Npgsql;

namespace Factory.Tests.Orchestration;

public sealed class OrchestrationFactAttribute : FactAttribute
{
    public OrchestrationFactAttribute()
    {
        if (new LifecycleFactAttribute().Skip is { } skip) Skip = skip;
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")))
            Skip = "Requires the restricted M7 orchestration worker role.";
    }
}

internal static class Durable
{
    internal static PostgresOrchestrationStore Api => new(LifecycleFixture.Runtime);
    internal static PostgresOrchestrationStore Worker => new(Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")!);
    internal static WorkflowRequest Request(LifecycleFixture f) => ReviewSamples.Request with { ProjectId = f.Project };
    internal static Task<RunSubmission> Submit(LifecycleFixture f, Guid? key = null) => Api.SubmitAsync(f.Actor, key ?? Guid.NewGuid(), Request(f));

    // The queue is database-wide; fail any leftover claimable work so each test sees only its runs.
    internal static async Task Drain()
    {
        await Sql("""UPDATE orchestration."StepAttempts" SET "LeaseExpiresAt"=now()-interval '1 second' WHERE "Outcome"='Running'""");
        for (var i = 0; i < 200; i++)
        {
            var token = RandomNumberGenerator.GetBytes(32);
            var step = await Worker.ClaimAsync("drain", token);
            if (step is null) return;
            await Worker.FailAsync(step.AttemptId, token, "generation_failed");
        }
    }

    internal static async Task ExpireLeases(Guid run)
        => await Sql("""UPDATE orchestration."StepAttempts" SET "LeaseExpiresAt"=now()-interval '1 second' WHERE "RunId"=@run AND "Outcome"='Running'""",
            new NpgsqlParameter("run", run));

    internal static async Task<int> Sql(string sql, params NpgsqlParameter[] parameters)
    {
        await using var db = new NpgsqlConnection(LifecycleFixture.Owner); await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db); command.Parameters.AddRange(parameters); return await command.ExecuteNonQueryAsync();
    }

    internal static async Task<object?> Scalar(string sql, params NpgsqlParameter[] parameters)
    {
        await using var db = new NpgsqlConnection(LifecycleFixture.Owner); await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db); command.Parameters.AddRange(parameters); return await command.ExecuteScalarAsync();
    }

    internal sealed class Provider : IModelProvider
    {
        internal int Analysis, Architecture; internal string? AnalysisContent;
        internal ModelUsage? AnalysisUsage, ArchitectureUsage; internal bool FailArchitecture;
        public Task<ModelResponse> GenerateAsync(ModelRequest request, CancellationToken ct = default)
        {
            if (request.OutputSchema!.Name == "analysis_result")
            {
                Interlocked.Increment(ref Analysis);
                return Task.FromResult(new ModelResponse(AnalysisContent ?? JsonSerializer.Serialize(ReviewSamples.Result.Analysis, AnalysisJson.Options), "test",
                    Usage: AnalysisUsage));
            }
            Interlocked.Increment(ref Architecture);
            if (FailArchitecture) throw new InvalidOperationException("synthetic provider failure");
            return Task.FromResult(new ModelResponse(JsonSerializer.Serialize(ReviewSamples.Result.Architecture, ArchitectureJson.Options), "test",
                Usage: ArchitectureUsage));
        }
    }
}

[Collection("Lifecycle database")]
public class DurableWorkflowPostgresTests
{
    [OrchestrationFact]
    public async Task SubmissionIsIdempotentConflictingAndBoundedPerProject()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); var key = Guid.NewGuid();
        var first = await Durable.Submit(f, key);
        Assert.Equal(RunStatus.Queued, first.Status); Assert.False(first.Replayed);
        var replay = await Durable.Submit(f, key);
        Assert.True(replay.Replayed); Assert.Equal(first.RunId, replay.RunId);
        var conflict = await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.SubmitAsync(f.Actor, key, Durable.Request(f) with { Context = "different" }));
        Assert.Equal(OrchestrationError.Conflict, conflict.Error);
        for (var i = 1; i < DurableWorkflowLimits.ActiveRunsPerProject; i++) await Durable.Submit(f);
        Assert.Equal(OrchestrationError.Capacity, (await Assert.ThrowsAsync<OrchestrationException>(() => Durable.Submit(f))).Error);
        Assert.True((await Durable.Submit(f, key)).Replayed);
        await Durable.Drain();
    }

    [OrchestrationFact]
    public async Task WorkerPublishesManagedCaseThatHumanCanReviewAndM6CanRead()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var run = await Durable.Submit(f); var provider = new Durable.Provider();
        var processor = new DurableWorkflowProcessor(Durable.Worker, provider);
        for (var i = 0; i < 3; i++) Assert.Equal(StepOutcome.Completed, await processor.ProcessNextAsync("worker-a", default));
        Assert.Equal(StepOutcome.Idle, await processor.ProcessNextAsync("worker-a", default));
        Assert.Equal((1, 1), (provider.Analysis, provider.Architecture));
        var view = await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId);
        Assert.Equal(RunStatus.Succeeded, view.Status); Assert.All(view.Steps, s => Assert.Equal((StepStatus.Succeeded, 1), (s.Status, s.Attempts)));
        var publication = view.Publication!;
        // The durable run identity is the M6 run, artifact workflow and work item lineage.
        var m6 = await f.Store.GetRunAsync(f.Actor, f.Project, run.RunId);
        Assert.Equal((GenerationStatus.Published, 201, publication.WorkItemId), (m6.Status, m6.HttpStatus, m6.WorkItemId));
        var work = await f.Store.GetWorkItemAsync(f.Actor, f.Project, publication.WorkItemId);
        Assert.Equal((run.RunId, (Guid?)null, 2L, publication.CaseId), (work.LatestRunId, work.ActiveRunId, work.Revision, work.CaseId!.Value));
        var artifact = await f.Decisions.GetArtifactAsync(f.Actor, f.Project, publication.CaseId, 1);
        Assert.Equal(publication.ContentHash, artifact.ContentHash);
        Assert.Equal(run.RunId, ReviewArtifactSerializer.Read(artifact).WorkflowId);
        var decided = await f.Decisions.DecideAsync(f.Actor, f.Project, publication.CaseId, new(1, 1, artifact.ContentHash, ReviewDecision.Approve, null, Guid.NewGuid()));
        Assert.Equal(ReviewState.Approved, decided.Snapshot.State);
        // Approval never schedules more work.
        Assert.Equal(StepOutcome.Idle, await processor.ProcessNextAsync("worker-a", default));
    }

    [OrchestrationFact]
    public async Task CrashedStepResumesFromCheckpointWithoutRepeatingCompletedAnalysis()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var run = await Durable.Submit(f); var provider = new Durable.Provider();
        var processor = new DurableWorkflowProcessor(Durable.Worker, provider);
        Assert.Equal(StepOutcome.Completed, await processor.ProcessNextAsync("worker-a", default));
        // Worker A leases Architecture and dies without completing it.
        var crashed = await Durable.Worker.ClaimAsync("worker-a", RandomNumberGenerator.GetBytes(32));
        Assert.Equal(StepKind.Architecture, crashed!.Kind); Assert.NotNull(crashed.Analysis);
        Assert.Null(await Durable.Worker.ClaimAsync("worker-b", RandomNumberGenerator.GetBytes(32)));
        await Durable.ExpireLeases(run.RunId);
        Assert.Equal(StepOutcome.Completed, await processor.ProcessNextAsync("worker-b", default));
        Assert.Equal(StepOutcome.Completed, await processor.ProcessNextAsync("worker-b", default));
        var view = await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId);
        Assert.Equal(RunStatus.Succeeded, view.Status);
        Assert.Equal([1, 2, 1], view.Steps.Select(s => s.Attempts));
        Assert.Equal((1, 1), (provider.Analysis, provider.Architecture));
    }

    [OrchestrationFact]
    public async Task StaleWorkerCannotCompleteRenewOrFailAfterReclaim()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var run = await Durable.Submit(f); var oldToken = RandomNumberGenerator.GetBytes(32);
        var old = await Durable.Worker.ClaimAsync("worker-a", oldToken);
        await Durable.ExpireLeases(run.RunId);
        var newToken = RandomNumberGenerator.GetBytes(32);
        var current = await Durable.Worker.ClaimAsync("worker-b", newToken);
        Assert.Equal(old!.Ordinal, current!.Ordinal); Assert.NotEqual(old.AttemptId, current.AttemptId);
        foreach (var call in new Func<Task>[] {
            () => Durable.Worker.CompleteAnalysisAsync(old.AttemptId, oldToken, ReviewSamples.Result.Analysis!),
            () => Durable.Worker.RenewAsync(old.AttemptId, oldToken), () => Durable.Worker.FailAsync(old.AttemptId, oldToken, "generation_failed") })
            Assert.Equal(OrchestrationError.LeaseLost, (await Assert.ThrowsAsync<OrchestrationException>(call)).Error);
        Assert.Equal(OrchestrationError.Forbidden, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Worker.CompleteAnalysisAsync(current.AttemptId, oldToken, ReviewSamples.Result.Analysis!))).Error);
        await Durable.Worker.RenewAsync(current.AttemptId, newToken);
        await Durable.Worker.CompleteAnalysisAsync(current.AttemptId, newToken, ReviewSamples.Result.Analysis!);
        Assert.Equal(StepStatus.Succeeded, (await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId)).Steps[0].Status);
        await Durable.Drain();
    }

    [OrchestrationFact]
    public async Task ConcurrentWorkersLeaseEachRunStepOnce()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var first = await Durable.Submit(f); var second = await Durable.Submit(f);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ClaimedStep?> Claim(int i) { await gate.Task; return await Durable.Worker.ClaimAsync($"worker-{i}", RandomNumberGenerator.GetBytes(32)); }
        var claims = Enumerable.Range(0, 4).Select(Claim).ToArray(); gate.SetResult();
        var results = (await Task.WhenAll(claims)).OfType<ClaimedStep>().ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal(new[] { first.RunId, second.RunId }.Order(), results.Select(r => r.RunId).Order());
        await Durable.Drain();
    }

    [OrchestrationFact]
    public async Task InvalidModelOutputFailsRunWithoutPublication()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var run = await Durable.Submit(f); var provider = new Durable.Provider { AnalysisContent = "{}" };
        Assert.Equal(StepOutcome.Failed, await new DurableWorkflowProcessor(Durable.Worker, provider).ProcessNextAsync("worker-a", default));
        var view = await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId);
        Assert.Equal((RunStatus.Failed, "invalid_output"), (view.Status, view.FailureCode)); Assert.Null(view.Publication);
        Assert.Equal([StepStatus.Failed, StepStatus.Cancelled, StepStatus.Cancelled], view.Steps.Select(s => s.Status));
        Assert.Equal(LifecycleError.NotFound, (await Assert.ThrowsAsync<LifecycleException>(() => f.Store.GetRunAsync(f.Actor, f.Project, run.RunId))).Error);
        Assert.Equal(StepOutcome.Idle, await new DurableWorkflowProcessor(Durable.Worker, provider).ProcessNextAsync("worker-a", default));
        Assert.Equal(1, provider.Analysis);
    }

    [OrchestrationFact]
    public async Task RepeatedLostLeasesExhaustTheStepAndFailTheRun()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); var run = await Durable.Submit(f);
        for (var i = 0; i < DurableWorkflowLimits.MaxModelStepAttempts; i++)
        {
            Assert.NotNull(await Durable.Worker.ClaimAsync("crashing", RandomNumberGenerator.GetBytes(32)));
            await Durable.ExpireLeases(run.RunId);
        }
        Assert.Null(await Durable.Worker.ClaimAsync("worker", RandomNumberGenerator.GetBytes(32)));
        var view = await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId);
        Assert.Equal((RunStatus.Failed, "step_attempts_exhausted", 2), (view.Status, view.FailureCode, view.Steps[0].Attempts));
    }

    [OrchestrationFact]
    public async Task RevokedRequesterCannotPublishAndNoCaseIsCreated()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); var run = await Durable.Submit(f);
        var processor = new DurableWorkflowProcessor(Durable.Worker, new Durable.Provider());
        await processor.ProcessNextAsync("worker", default); await processor.ProcessNextAsync("worker", default);
        await f.Sql("""UPDATE access."ProjectMemberships" SET "IsActive"=false,"Revision"="Revision"+1 WHERE "ProjectId"=@project AND "Role"='Generator'""",
            new NpgsqlParameter("project", f.Project));
        Assert.Equal(StepOutcome.Failed, await processor.ProcessNextAsync("worker", default));
        Assert.Equal(0L, await Durable.Scalar("""
            SELECT (SELECT count(*) FROM review."ReviewCases" c JOIN orchestration."WorkflowRuns" r ON r."CaseId"=c."Id" WHERE r."Id"=@run)
                 + (SELECT count(*) FROM lifecycle."GenerationCommands" WHERE "RunId"=@run)
            """, new NpgsqlParameter("run", run.RunId)));
        await f.Sql("""UPDATE access."ProjectMemberships" SET "IsActive"=true,"Revision"="Revision"+1 WHERE "ProjectId"=@project AND "Role"='Generator'""",
            new NpgsqlParameter("project", f.Project));
        var view = await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId);
        Assert.Equal((RunStatus.Failed, "publication_forbidden"), (view.Status, view.FailureCode));
    }

    [OrchestrationFact]
    public async Task DatabaseRejectsArtifactThatDiffersFromCheckpoints()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); var run = await Durable.Submit(f);
        var processor = new DurableWorkflowProcessor(Durable.Worker, new Durable.Provider());
        await processor.ProcessNextAsync("worker", default); await processor.ProcessNextAsync("worker", default);
        var token = RandomNumberGenerator.GetBytes(32);
        var step = (await Durable.Worker.ClaimAsync("worker", token))!;
        var tampered = new WorkflowResult(run.RunId, WorkflowStatus.AwaitingHumanReview, WorkflowStageStatus.Completed, WorkflowStageStatus.Completed,
            step.Analysis! with { Summary = "Silently changed after checkpoint" }, step.Architecture);
        var bytes = ReviewArtifactSerializer.Create(step.CaseId, 1, step.Request, tampered, DateTimeOffset.UtcNow).GetContent();
        await using var db = new NpgsqlConnection(Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")); await db.OpenAsync();
        await using var publish = new NpgsqlCommand("SELECT orchestration.publish(@a,@t,@b)", db);
        publish.Parameters.AddWithValue("a", step.AttemptId); publish.Parameters.AddWithValue("t", token); publish.Parameters.AddWithValue("b", bytes);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => publish.ExecuteScalarAsync())).SqlState);
        await Durable.Worker.PublishAsync(step, token);
        Assert.Equal(RunStatus.Succeeded, (await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId)).Status);
    }

    [OrchestrationFact]
    public async Task RolesAreSeparatedAndCannotTouchTablesDirectly()
    {
        await using var worker = new NpgsqlConnection(Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")); await worker.OpenAsync();
        foreach (var sql in new[] { """SELECT * FROM orchestration."WorkflowRuns" """, """UPDATE orchestration."StepAttempts" SET "Outcome"='Succeeded'""",
            """SELECT * FROM lifecycle."GenerationCommands" """, """SELECT * FROM review."ArtifactVersions" """, """SELECT * FROM access."Principals" """,
            "SELECT orchestration.submit('p','i','s','Human',gen_random_uuid(),'analysis-architecture-v1','{}'::jsonb)",
            "SELECT orchestration.read_run('p','i','s','Human',gen_random_uuid())", "SELECT orchestration.fail_run(gen_random_uuid(),0,'x')",
            "SELECT lifecycle.admit('p','i','s','Human',gen_random_uuid(),NULL,NULL,'v','{}'::jsonb,'\\x00'::bytea)",
            "CREATE TABLE public.forbidden(id int)", "SET ROLE factory_lifecycle_guard" })
        {
            await using var command = new NpgsqlCommand(sql, worker);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
        await using var runtime = new NpgsqlConnection(LifecycleFixture.Runtime); await runtime.OpenAsync();
        foreach (var sql in new[] { """SELECT * FROM orchestration."WorkflowSteps" """, "SELECT orchestration.claim('w','\\x00'::bytea)",
            "SELECT orchestration.publish(gen_random_uuid(),'\\x00'::bytea,'\\x00'::bytea)" })
        {
            await using var command = new NpgsqlCommand(sql, runtime);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
    }

    [OrchestrationFact]
    public async Task ReadsAreProjectScopedAndUnknownRunsAreNotFound()
    {
        await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        var run = await Durable.Submit(f);
        Assert.Equal(OrchestrationError.NotFound, (await Assert.ThrowsAsync<OrchestrationException>(() => Durable.Api.GetRunAsync(other.Actor, other.Project, run.RunId))).Error);
        Assert.Equal(OrchestrationError.Forbidden, (await Assert.ThrowsAsync<OrchestrationException>(() => Durable.Api.GetRunAsync(other.Actor, f.Project, run.RunId))).Error);
        Assert.Equal(OrchestrationError.NotFound, (await Assert.ThrowsAsync<OrchestrationException>(() => Durable.Api.GetRunAsync(f.Actor, f.Project, Guid.NewGuid()))).Error);
        Assert.Equal(OrchestrationError.Forbidden, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.SubmitAsync(other.Actor, Guid.NewGuid(), Durable.Request(f)))).Error);
        await Durable.Drain();
    }
}
