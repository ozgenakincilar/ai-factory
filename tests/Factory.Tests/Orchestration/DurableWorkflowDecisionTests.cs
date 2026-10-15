using System.Security.Cryptography;
using Factory.Core.Authorization;
using Factory.Core.Orchestration;
using Factory.Orchestration.Durable;
using Factory.Tests.Lifecycle;
using Factory.Tests.Reviews;
using Npgsql;
using NpgsqlTypes;

namespace Factory.Tests.Orchestration;

[Collection("Lifecycle database")]
public class DurableWorkflowDecisionTests
{
    private static readonly string[] Options = ["use_factory_model", "stop"];

    private static async Task<(RunSubmission Run, ClaimedStep Step, byte[] Token, Guid Decision)> Waiting(LifecycleFixture f)
    {
        var run = await Durable.Submit(f); var token = RandomNumberGenerator.GetBytes(32);
        var step = (await Durable.Worker.ClaimAsync("worker", token))!;
        Assert.Equal(run.RunId, step.RunId);
        var decision = await Durable.Worker.RequestDecisionAsync(step.AttemptId, token, "customer_model_insufficient", Options);
        return (run, step, token, decision);
    }

    [OrchestrationFact]
    public async Task WaitingStepHoldsNoLeaseAndResumesOnlyAfterHumanDecision()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var (run, step, token, decision) = await Waiting(f);
        var view = await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId);
        Assert.Equal((RunStatus.Waiting, StepStatus.Waiting), (view.Status, view.Steps[0].Status));
        var pending = Assert.Single(view.Decisions!);
        Assert.Equal((decision, DecisionStatus.Pending, "customer_model_insufficient"), (pending.Id, pending.Status, pending.Reason));
        Assert.Equal(Options, pending.Options);
        // No lease, no timeout: expired-lease recovery and other workers leave it untouched.
        Assert.Equal(OrchestrationError.LeaseLost, (await Assert.ThrowsAsync<OrchestrationException>(() => Durable.Worker.RenewAsync(step.AttemptId, token))).Error);
        await Durable.ExpireLeases(run.RunId);
        Assert.Null(await Durable.Worker.ClaimAsync("other", RandomNumberGenerator.GetBytes(32)));
        Assert.Equal(RunStatus.Waiting, (await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId)).Status);

        var decided = await Durable.Api.DecideAsync(f.Actor, f.Project, run.RunId, decision, "use_factory_model");
        Assert.Equal((RunStatus.Running, StepStatus.Pending), (decided.Status, decided.Steps[0].Status));
        Assert.Equal(("use_factory_model", DecisionStatus.Decided), (decided.Decisions![0].Choice, decided.Decisions[0].Status));
        Assert.Equal(decided.Status, (await Durable.Api.DecideAsync(f.Actor, f.Project, run.RunId, decision, "use_factory_model")).Status);
        Assert.Equal(OrchestrationError.Conflict, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.DecideAsync(f.Actor, f.Project, run.RunId, decision, "stop"))).Error);

        var resumedToken = RandomNumberGenerator.GetBytes(32);
        var resumed = (await Durable.Worker.ClaimAsync("worker", resumedToken))!;
        Assert.Equal((0, "use_factory_model"), (resumed.Ordinal, Assert.Single(resumed.Decisions!).Choice));
        Assert.Equal((2, 0), (resumed.Steps[0].Attempts, resumed.Steps[0].Abandoned));
        await Durable.Worker.CompleteAnalysisAsync(resumed.AttemptId, resumedToken, ReviewSamples.Result.Analysis!);
        var processor = new DurableWorkflowProcessor(Durable.Worker, new Durable.Provider());
        Assert.Equal(StepOutcome.Completed, await processor.ProcessNextAsync("worker", default));
        Assert.Equal(StepOutcome.Completed, await processor.ProcessNextAsync("worker", default));
        Assert.Equal(RunStatus.Succeeded, (await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId)).Status);
    }

    [OrchestrationFact]
    public async Task StopDecisionCancelsTheRunAndCancelledRunsAreNeverClaimed()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var (run, _, _, decision) = await Waiting(f);
        var stopped = await Durable.Api.DecideAsync(f.Actor, f.Project, run.RunId, decision, "stop");
        Assert.Equal(RunStatus.Cancelled, stopped.Status);
        Assert.All(stopped.Steps, s => Assert.Equal(StepStatus.Cancelled, s.Status));
        Assert.Equal(("stop", DecisionStatus.Decided), (stopped.Decisions![0].Choice, stopped.Decisions[0].Status));
        Assert.Null(await Durable.Worker.ClaimAsync("worker", RandomNumberGenerator.GetBytes(32)));
        Assert.Equal(RunStatus.Cancelled, (await Durable.Api.CancelAsync(f.Actor, f.Project, run.RunId)).Status);
    }

    [OrchestrationFact]
    public async Task CancellationRevokesTheRunningLeaseAndFinishedRunsCannotBeCancelled()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var run = await Durable.Submit(f); var token = RandomNumberGenerator.GetBytes(32);
        var step = (await Durable.Worker.ClaimAsync("worker", token))!;
        var cancelled = await Durable.Api.CancelAsync(f.Actor, f.Project, run.RunId);
        Assert.Equal(RunStatus.Cancelled, cancelled.Status);
        foreach (var call in new Func<Task>[] {
            () => Durable.Worker.CompleteAnalysisAsync(step.AttemptId, token, ReviewSamples.Result.Analysis!),
            () => Durable.Worker.RenewAsync(step.AttemptId, token),
            () => Durable.Worker.RequestDecisionAsync(step.AttemptId, token, "customer_model_insufficient", Options) })
            Assert.Equal(OrchestrationError.LeaseLost, (await Assert.ThrowsAsync<OrchestrationException>(call)).Error);
        Assert.Equal(RunStatus.Cancelled, (await Durable.Api.CancelAsync(f.Actor, f.Project, run.RunId)).Status);

        var finished = await Durable.Submit(f); var processor = new DurableWorkflowProcessor(Durable.Worker, new Durable.Provider());
        for (var i = 0; i < 3; i++) await processor.ProcessNextAsync("worker", default);
        Assert.Equal(OrchestrationError.Conflict, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.CancelAsync(f.Actor, f.Project, finished.RunId))).Error);
        Assert.Equal(RunStatus.Succeeded, (await Durable.Api.GetRunAsync(f.Actor, f.Project, finished.RunId)).Status);
    }

    [OrchestrationFact]
    public async Task OnlyActiveHumanReviewersDecideAndOnlyOfferedChoices()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); await using var service = await LifecycleFixture.Create(PrincipalKind.Service);
        await using var other = await LifecycleFixture.Create();
        var (run, _, _, decision) = await Waiting(f);
        await f.Sql("""INSERT INTO access."ProjectMemberships" ("ProjectId","PrincipalId","PrincipalKind","Role","IsActive","Revision") VALUES (@p,@a,'Service','Generator',true,1)""",
            new NpgsqlParameter("p", f.Project), new NpgsqlParameter("a", service.PrincipalId));
        async Task<OrchestrationError> Decide(TrustedIdentity actor, string choice = "use_factory_model", Guid? id = null)
            => (await Assert.ThrowsAsync<OrchestrationException>(() => Durable.Api.DecideAsync(actor, f.Project, run.RunId, id ?? decision, choice))).Error;
        Assert.Equal(OrchestrationError.Forbidden, await Decide(service.Actor));
        Assert.Equal(OrchestrationError.Forbidden, await Decide(other.Actor));
        Assert.Equal(OrchestrationError.InvalidCommand, await Decide(f.Actor, "retry_later"));
        Assert.Equal(OrchestrationError.NotFound, await Decide(f.Actor, id: Guid.NewGuid()));
        await f.Sql("""UPDATE access."ProjectMemberships" SET "IsActive"=false,"Revision"="Revision"+1 WHERE "ProjectId"=@p AND "Role"='Reviewer'""", new NpgsqlParameter("p", f.Project));
        Assert.Equal(OrchestrationError.Forbidden, await Decide(f.Actor));
        await f.Sql("""UPDATE access."ProjectMemberships" SET "IsActive"=true,"Revision"="Revision"+1 WHERE "ProjectId"=@p AND "Role"='Reviewer'""", new NpgsqlParameter("p", f.Project));
        Assert.Equal(RunStatus.Waiting, (await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId)).Status);
        Assert.Equal(RunStatus.Cancelled, (await Durable.Api.CancelAsync(f.Actor, f.Project, run.RunId)).Status);
        Assert.Equal(DecisionStatus.Cancelled, (await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId)).Decisions![0].Status);
    }

    [OrchestrationFact]
    public async Task DatabaseRejectsInvalidDecisionRequestsAndWaitingPublication()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); await Durable.Submit(f);
        var token = RandomNumberGenerator.GetBytes(32); var step = (await Durable.Worker.ClaimAsync("worker", token))!;
        await using var db = new NpgsqlConnection(Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")); await db.OpenAsync();
        async Task<string> Request(string reason, string[] options)
        {
            await using var command = new NpgsqlCommand("SELECT orchestration.request_decision(@a,@t,@r,@o)", db);
            command.Parameters.AddWithValue("a", step.AttemptId); command.Parameters.AddWithValue("t", token); command.Parameters.AddWithValue("r", reason);
            command.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = options });
            return (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync())).SqlState;
        }
        Assert.Equal("22023", await Request("customer_model_insufficient", ["use_factory_model", "retry_later"]));
        Assert.Equal("22023", await Request("Customer text", ["use_factory_model", "stop"]));
        Assert.Equal("22023", await Request("customer_model_insufficient", ["stop", "stop"]));
        await Durable.Worker.CompleteAnalysisAsync(step.AttemptId, token, ReviewSamples.Result.Analysis!);
        await new DurableWorkflowProcessor(Durable.Worker, new Durable.Provider()).ProcessNextAsync("worker", default);
        var publicationToken = RandomNumberGenerator.GetBytes(32);
        var publication = (await Durable.Worker.ClaimAsync("worker", publicationToken))!;
        Assert.Equal(StepKind.Publication, publication.Kind);
        Assert.Equal(OrchestrationError.InvalidCommand, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Worker.RequestDecisionAsync(publication.AttemptId, publicationToken, "customer_model_insufficient", Options))).Error);
        await Durable.Worker.PublishAsync(publication, publicationToken);
    }

    [OrchestrationFact]
    public async Task DecisionAndCancellationFunctionsAreRoleSeparated()
    {
        await using var worker = new NpgsqlConnection(Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")); await worker.OpenAsync();
        foreach (var sql in new[] { "SELECT orchestration.cancel('p','i','s','Human',gen_random_uuid())",
            "SELECT orchestration.decide('p','i','s','Human',gen_random_uuid(),gen_random_uuid(),'stop')",
            "SELECT orchestration.cancel_run(gen_random_uuid(),gen_random_uuid())", """SELECT * FROM orchestration."StepDecisions" """ })
        {
            await using var command = new NpgsqlCommand(sql, worker);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
        await using var runtime = new NpgsqlConnection(LifecycleFixture.Runtime); await runtime.OpenAsync();
        foreach (var sql in new[] { "SELECT orchestration.request_decision(gen_random_uuid(),'\\x00'::bytea,'x',ARRAY['stop','x'])",
            """UPDATE orchestration."StepDecisions" SET "Status"='Decided'""" })
        {
            await using var command = new NpgsqlCommand(sql, runtime);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
    }
}
