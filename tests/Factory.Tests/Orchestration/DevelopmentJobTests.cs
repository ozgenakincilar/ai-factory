using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Core.Development;
using Factory.Core.Execution;
using Factory.Core.Orchestration;
using Factory.Core.Reviews;
using Factory.Core.Templates;
using Factory.Infrastructure.Templates;
using Factory.Orchestration.Development;
using Factory.Orchestration.Durable;
using Factory.Tests.Integration;
using Factory.Tests.Lifecycle;
using Factory.Tests.Templates;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using StepOutcome = Factory.Core.Execution.StepOutcome;

namespace Factory.Tests.Orchestration;

[Collection("Lifecycle database")]
public class DevelopmentJobTests
{
    private const string Template = "dotnet-angular-postgres-ddd";
    private const long Cap = 400_000;
    private const long Unlimited = long.MaxValue / 4;

    private static async Task<RunPublication> Published(LifecycleFixture f)
    {
        var run = await Durable.Submit(f);
        var processor = new DurableWorkflowProcessor(Durable.Worker, new Durable.Provider());
        for (var i = 0; i < 3; i++) await processor.ProcessNextAsync("worker", default);
        return (await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId)).Publication!;
    }

    private static async Task<(RunPublication Publication, string Hash)> Approved(LifecycleFixture f)
    {
        var published = await Published(f);
        var artifact = await f.Decisions.GetArtifactAsync(f.Actor, f.Project, published.CaseId, 1);
        await f.Decisions.DecideAsync(f.Actor, f.Project, published.CaseId, new DecisionCommand(1, 1, artifact.ContentHash, ReviewDecision.Approve, null, Guid.NewGuid()));
        return (published, artifact.ContentHash);
    }

    private static DevelopmentRequest Request(Guid caseId, string hash, int version = 1) => new(caseId, version, hash, Template, "1.0.0", "SampleShop");

    // The queue is database-wide: end leftover jobs so each test sees only its own.
    private static async Task Drain()
    {
        await Durable.Sql("""UPDATE orchestration."DevelopmentJobs" SET "LeaseExpiresAt"=now()-interval '1 second' WHERE "Status"='Running'""");
        for (var i = 0; i < 100; i++)
        {
            var token = RandomNumberGenerator.GetBytes(32);
            var job = await Durable.Worker.ClaimDevelopmentAsync("drain", token, Cap, Unlimited);
            if (job is null) return;
            await Durable.Worker.FailDevelopmentAsync(job.JobId, token, "invalid_artifact");
        }
    }

    private static async Task<long> Committed() => Convert.ToInt64(await Durable.Scalar("""
        SELECT COALESCE((SELECT sum("CostMicros") FROM orchestration."DevelopmentEvidence"),0)
             + COALESCE((SELECT sum("CapMicros") FROM orchestration."DevelopmentJobs" WHERE "Status"='Running' OR ("Status"='Failed' AND "FailureCode"='worker_lost')),0)
        """));

    private static OrchestrationError Error(Exception ex) => Assert.IsType<OrchestrationException>(ex).Error;

    private static DevelopmentEvidence Evidence(ClaimedDevelopment job, DevelopmentJobStatus status = DevelopmentJobStatus.Verified, long cost = 9_154,
        string? project = null, bool diff = true)
        => new(1, job.JobId, project ?? job.ProjectId, job.CaseId, job.ArtifactVersion, job.ArtifactSha256, job.TemplateId, job.TemplateVersion,
            new string('a', 64), job.ApplicationName, "gpt-5.4-mini", status, status == DevelopmentJobStatus.Verified ? null : "step_failed", 3, 12, cost,
            [new EvidenceCall(1, "completed", "gpt-5.4-mini-2026-03-17", 952, 247, 55_881, 1_826)],
            [new FileChange("src/SampleShop.Domain/Product.cs", FileChangeKind.Added, ["FR-1"])],
            diff ? new string('b', 40) : null, diff ? new string('c', 40) : null, diff ? "diff --git a/x b/x" : null, diff ? new string('d', 64) : null,
            new SandboxReport("sha256:" + new string('e', 64), new string('f', 64), SandboxLimits.Default, [],
                status == DevelopmentJobStatus.Verified ? SandboxOutcome.Passed : SandboxOutcome.Failed,
                status == DevelopmentJobStatus.Verified ? null : "step_failed", DateTimeOffset.UnixEpoch, TimeSpan.Zero),
            ["turn 1: plan"]);

    [OrchestrationFact]
    public async Task OnlyTheCurrentApprovedVersion_CanBeSubmitted_IdempotentlyAndPerProject()
    {
        await Drain(); await Durable.Drain();
        await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        var pending = await Published(f);
        var pendingHash = (await f.Decisions.GetArtifactAsync(f.Actor, f.Project, pending.CaseId, 1)).ContentHash;
        Assert.Equal(OrchestrationError.StaleRevision, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, Guid.NewGuid(), Request(pending.CaseId, pendingHash)))));
        var (approved, hash) = await Approved(f);
        Assert.Equal(OrchestrationError.StaleRevision, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, Guid.NewGuid(), Request(approved.CaseId, new string('0', 64))))));
        Assert.Equal(OrchestrationError.StaleRevision, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, Guid.NewGuid(), Request(approved.CaseId, hash, version: 2)))));
        Assert.Equal(OrchestrationError.NotFound, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, Guid.NewGuid(), Request(Guid.NewGuid(), hash)))));
        Assert.Equal(OrchestrationError.Forbidden, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitDevelopmentAsync(other.Actor, f.Project, Guid.NewGuid(), Request(approved.CaseId, hash)))));
        // Another project's case is not found from this project.
        Assert.Equal(OrchestrationError.NotFound, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitDevelopmentAsync(other.Actor, other.Project, Guid.NewGuid(), Request(approved.CaseId, hash)))));
        Assert.Equal(OrchestrationError.InvalidCommand, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, Guid.NewGuid(), Request(approved.CaseId, hash) with { ApplicationName = "bad name" }))));

        var key = Guid.NewGuid();
        var first = await Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, key, Request(approved.CaseId, hash));
        Assert.Equal((DevelopmentJobStatus.Queued, false), (first.Status, first.Replayed));
        Assert.Equal((first.JobId, true), ((await Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, key, Request(approved.CaseId, hash))) is var r ? (r.JobId, r.Replayed) : default));
        Assert.Equal(OrchestrationError.Conflict, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, key, Request(approved.CaseId, hash) with { ApplicationName = "OtherShop" }))));
        await Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, Guid.NewGuid(), Request(approved.CaseId, hash));
        Assert.Equal(OrchestrationError.Capacity, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, Guid.NewGuid(), Request(approved.CaseId, hash)))));
        var listed = await Durable.Api.ListDevelopmentsAsync(f.Actor, f.Project);
        Assert.Equal(2, listed.Count);
        Assert.All(listed, j => Assert.Equal((approved.CaseId, 1, DevelopmentJobStatus.Queued, (long?)null), (j.CaseId, j.ArtifactVersion, j.Status, j.CostMicros)));
        Assert.Equal(OrchestrationError.Forbidden, Error(await Assert.ThrowsAnyAsync<Exception>(() => Durable.Api.ListDevelopmentsAsync(other.Actor, f.Project))));
        await Drain();
    }

    [OrchestrationFact]
    public async Task Claims_RecheckTheApproval_AndTheTotalCap_BeforeAnyWork()
    {
        await Drain(); await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var (approved, hash) = await Approved(f);
        // The approved version is superseded by a regeneration (version 2 pending) before a worker claims.
        var stale = await Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, Guid.NewGuid(), Request(approved.CaseId, hash));
        var work = await f.Store.GetWorkItemAsync(f.Actor, f.Project, approved.WorkItemId);
        await Durable.Api.SubmitRegenerationAsync(f.Actor, Guid.NewGuid(), work.WorkItemId, work.ETag, Durable.Request(f));
        var processor = new DurableWorkflowProcessor(Durable.Worker, new Durable.Provider());
        for (var i = 0; i < 3; i++) await processor.ProcessNextAsync("worker", default);
        Assert.Null(await Durable.Worker.ClaimDevelopmentAsync("worker", RandomNumberGenerator.GetBytes(32), Cap, Unlimited));
        Assert.Equal((DevelopmentJobStatus.Failed, "approval_changed"),
            ((await Durable.Api.GetDevelopmentAsync(f.Actor, f.Project, stale.JobId)).Job is var j1 ? (j1.Status, j1.FailureCode) : default));

        // The total cap counts recorded spend and running jobs' caps: one cap short of room refuses the job.
        var (fresh, freshHash) = await Approved(f);
        var refused = await Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, Guid.NewGuid(), Request(fresh.CaseId, freshHash));
        Assert.Null(await Durable.Worker.ClaimDevelopmentAsync("worker", RandomNumberGenerator.GetBytes(32), Cap, await Committed() + Cap - 1));
        Assert.Equal((DevelopmentJobStatus.BudgetExhausted, "total_cap_reached"),
            ((await Durable.Api.GetDevelopmentAsync(f.Actor, f.Project, refused.JobId)).Job is var j2 ? (j2.Status, j2.FailureCode) : default));
        var admitted = await Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, Guid.NewGuid(), Request(fresh.CaseId, freshHash));
        var token = RandomNumberGenerator.GetBytes(32);
        var claimed = (await Durable.Worker.ClaimDevelopmentAsync("worker", token, Cap, await Committed() + Cap))!;
        Assert.Equal((admitted.JobId, f.Project, fresh.CaseId, 1, freshHash), (claimed.JobId, claimed.ProjectId, claimed.CaseId, claimed.ArtifactVersion, claimed.ArtifactSha256));
        // The worker receives the exact approved bytes.
        Assert.Equal(freshHash, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(claimed.Artifact))));
        // A lost worker ends the job (no paid re-run) and its full cap stays counted as spent.
        var before = await Committed();
        await Durable.Sql("""UPDATE orchestration."DevelopmentJobs" SET "LeaseExpiresAt"=now()-interval '1 second' WHERE "Id"=@id""", new NpgsqlParameter("id", claimed.JobId));
        Assert.Null(await Durable.Worker.ClaimDevelopmentAsync("worker", RandomNumberGenerator.GetBytes(32), Cap, Unlimited));
        Assert.Equal((DevelopmentJobStatus.Failed, "worker_lost"),
            ((await Durable.Api.GetDevelopmentAsync(f.Actor, f.Project, claimed.JobId)).Job is var j3 ? (j3.Status, j3.FailureCode) : default));
        Assert.Equal(before, await Committed());
        Assert.Equal(OrchestrationError.LeaseLost, Error(await Assert.ThrowsAnyAsync<Exception>(() => Durable.Worker.RenewDevelopmentAsync(claimed.JobId, token))));
    }

    [OrchestrationFact]
    public async Task Completion_StoresImmutableEvidence_ThatDescribesThisJobWithinItsCap()
    {
        await Drain(); await Durable.Drain(); await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        var (approved, hash) = await Approved(f);
        var submitted = await Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, Guid.NewGuid(), Request(approved.CaseId, hash));
        var token = RandomNumberGenerator.GetBytes(32);
        var job = (await Durable.Worker.ClaimDevelopmentAsync("worker", token, Cap, Unlimited))!;
        foreach (var invalid in new[]
        {
            Evidence(job, project: other.Project), Evidence(job, cost: Cap + 1), Evidence(job) with { ArtifactSha256 = new string('0', 64) },
            Evidence(job, diff: false), Evidence(job) with { FailureCode = "x" }, Evidence(job) with { FormatVersion = 2 },
        })
            Assert.Equal(OrchestrationError.InvalidCommand, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
                Durable.Worker.CompleteDevelopmentAsync(job.JobId, token, invalid))));
        Assert.Equal(OrchestrationError.Forbidden, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Worker.CompleteDevelopmentAsync(job.JobId, RandomNumberGenerator.GetBytes(32), Evidence(job)))));
        await Durable.Worker.CompleteDevelopmentAsync(job.JobId, token, Evidence(job));
        var view = await Durable.Api.GetDevelopmentAsync(f.Actor, f.Project, submitted.JobId);
        Assert.Equal((DevelopmentJobStatus.Verified, (string?)null, (long?)9_154), (view.Job.Status, view.Job.FailureCode, view.Job.CostMicros));
        var bytes = await Durable.Api.GetDevelopmentEvidenceAsync(f.Actor, f.Project, submitted.JobId);
        Assert.Equal(DevelopmentEvidenceFormat.Serialize(Evidence(job)), bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), view.EvidenceSha256);
        Assert.Equal(["FR-1"], view.Evidence!.Changes.Single().RequirementIds);
        Assert.Equal(OrchestrationError.Forbidden, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.GetDevelopmentEvidenceAsync(other.Actor, f.Project, submitted.JobId))));
        Assert.Equal(OrchestrationError.LeaseLost, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Worker.CompleteDevelopmentAsync(job.JobId, token, Evidence(job)))));
        foreach (var sql in new[]
        {
            """UPDATE orchestration."DevelopmentEvidence" SET "CostMicros"=0""", """DELETE FROM orchestration."DevelopmentEvidence" """,
            """UPDATE orchestration."DevelopmentJobs" SET "Status"='Failed',"FailureCode"='x' WHERE "Status"='Verified'""",
            """DELETE FROM orchestration."DevelopmentJobs" """,
        })
            await Assert.ThrowsAsync<PostgresException>(() => Durable.Sql(sql));
    }

    [OrchestrationFact]
    public async Task PullRequestOutcomes_AreRecordedOnce_ForTheEvidencedHeadOnly()
    {
        await Drain(); await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var (approved, hash) = await Approved(f);
        var submitted = await Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, Guid.NewGuid(), Request(approved.CaseId, hash));
        var token = RandomNumberGenerator.GetBytes(32);
        var job = (await Durable.Worker.ClaimDevelopmentAsync("worker", token, Cap, Unlimited))!;
        await Durable.Worker.CompleteDevelopmentAsync(job.JobId, token, Evidence(job));
        // The queue is database-wide: settle other verified jobs first.
        PendingPullRequest? next;
        while ((next = await Durable.Worker.NextPullRequestAsync()) is not null && next.JobId != job.JobId)
            await Durable.Worker.RecordPullRequestAsync(next.JobId, "ozgenakincilar/stockflow", "factory/test-drain", null, "test_drain");
        Assert.NotNull(next);
        Assert.Equal(DevelopmentEvidenceFormat.Serialize(Evidence(job)), next!.Evidence);
        const string repo = "ozgenakincilar/stockflow";
        var head = Evidence(job).FinalCommit!;
        foreach (var (url, commit) in new[] { ("https://github.com/ozgenakincilar/stockflow/pull/1", new string('9', 40)),
            ("https://github.com/someone/else/pull/1", head), ("https://evil.example/ozgenakincilar/stockflow/pull/1", head) })
            Assert.Equal(OrchestrationError.InvalidCommand, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
                Durable.Worker.RecordPullRequestAsync(job.JobId, repo, "factory/sample-shop-1", new PullRequestResult(1, url, commit), null))));
        // Only factory/ branches can be recorded (a check constraint, surfaced as an integrity error).
        await Assert.ThrowsAnyAsync<OrchestrationException>(() =>
            Durable.Worker.RecordPullRequestAsync(job.JobId, repo, "main", new PullRequestResult(1, "https://github.com/ozgenakincilar/stockflow/pull/1", head), null));
        await Durable.Worker.RecordPullRequestAsync(job.JobId, repo, "factory/sample-shop-1", new PullRequestResult(1, "https://github.com/ozgenakincilar/stockflow/pull/1", head), null);
        Assert.Equal(OrchestrationError.Conflict, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Worker.RecordPullRequestAsync(job.JobId, repo, "factory/sample-shop-1", null, "push_failed"))));
        Assert.NotEqual(job.JobId, (await Durable.Worker.NextPullRequestAsync())?.JobId);
        var view = await Durable.Api.GetDevelopmentAsync(f.Actor, f.Project, submitted.JobId);
        Assert.Equal(("Opened", (int?)1, "https://github.com/ozgenakincilar/stockflow/pull/1", head),
            (view.PullRequest!.Status, view.PullRequest.Number, view.PullRequest.Url, view.PullRequest.HeadCommit));
        await Assert.ThrowsAsync<PostgresException>(() => Durable.Sql("UPDATE orchestration.\"DevelopmentPullRequests\" SET \"Number\"=2"));
        await Assert.ThrowsAsync<PostgresException>(() => Durable.Sql("DELETE FROM orchestration.\"DevelopmentPullRequests\""));
        await using var db = new NpgsqlConnection(LifecycleFixture.Runtime); await db.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT orchestration.next_pull_request()", db);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync())).SqlState);
    }

    [OrchestrationFact]
    public async Task RuntimeAndWorkerRoles_ReachOnlyTheirOwnFunctions()
    {
        var worker = Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")!;
        foreach (var (connection, sql) in new[]
        {
            (worker, "SELECT orchestration.submit_development('p','i','s','Human',gen_random_uuid(),gen_random_uuid(),1,repeat('a',64),'t','1','SampleShop')"),
            (worker, "SELECT orchestration.read_development_evidence('p','i','s','Human',gen_random_uuid())"),
            (worker, """SELECT count(*) FROM orchestration."DevelopmentJobs" """),
            (LifecycleFixture.Runtime, "SELECT orchestration.claim_development('w',decode(repeat('00',32),'hex'),1,1)"),
            (LifecycleFixture.Runtime, "SELECT orchestration.approved_artifact('p',gen_random_uuid(),1,repeat('a',64))"),
            (LifecycleFixture.Runtime, """SELECT count(*) FROM orchestration."DevelopmentEvidence" """),
        })
        {
            await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
            await using var command = new NpgsqlCommand(sql, db);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync())).SqlState);
        }
    }

    private sealed class FakeSession : IWorkspaceSession
    {
        public Task WriteAsync(string path, string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task<StepResult> RunAsync(SandboxStep step, CancellationToken ct = default)
            => Task.FromResult(new StepResult(step.Name, StepOutcome.Passed, 0, TimeSpan.Zero, "ok", false));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Fakes : IWorkspaceRunner, ISandbox, IWorkspaceDiffer
    {
        public Task<IWorkspaceSession> StartAsync(IReadOnlyList<TemplateFile> files, CancellationToken ct = default) => Task.FromResult<IWorkspaceSession>(new FakeSession());
        public Task<SandboxReport> RunAsync(IReadOnlyList<TemplateFile> workspace, IReadOnlyList<SandboxStep> steps, CancellationToken ct = default)
            => Task.FromResult(DeveloperScripts.Report(workspace, SandboxOutcome.Passed));
        public Task<WorkspaceDiff> CreateAsync(IReadOnlyList<TemplateFile> baseFiles, IReadOnlyList<TemplateFile> finalFiles, CancellationToken ct = default)
            => Task.FromResult(new WorkspaceDiff(new string('1', 40), new string('2', 40), "diff --git a/src b/src", new string('3', 64)));
    }

    private static DevelopmentProcessor Processor(ScriptedModel model)
    {
        var fakes = new Fakes();
        return new DevelopmentProcessor(Durable.Worker, new FileSystemTemplateCatalog(TemplateRepository.Root()),
            new DevelopmentModel("gpt-5.4-mini", model, 750_000, 4_500_000), fakes, fakes, fakes, DevelopmentLimits.Default, Unlimited);
    }

    [OrchestrationFact]
    public async Task Processor_ImplementsTheApprovedArtifact_AndRecordsEvidence()
    {
        await Drain(); await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var (approved, hash) = await Approved(f);
        var job = await Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, Guid.NewGuid(), Request(approved.CaseId, hash));
        var model = new ScriptedModel(
            DeveloperScripts.Turn(DeveloperScripts.Write("src/SampleShop.Domain/Tasks/TaskItem.cs", string.Format(DeveloperScripts.TaskItem, "SampleShop"), "FR-1"),
                DeveloperScripts.Act(DeveloperActionKind.Build), DeveloperScripts.Act(DeveloperActionKind.Test)),
            DeveloperScripts.Turn(new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "done")));
        Assert.Equal(DevelopmentOutcome.Completed, await Processor(model).ProcessNextAsync("worker", default));
        // The approved analysis reached the model: its requirement ids are in the prompt.
        Assert.Contains("\"FR-1\"", model.Prompts[0]);
        var view = await Durable.Api.GetDevelopmentAsync(f.Actor, f.Project, job.JobId);
        Assert.Equal((DevelopmentJobStatus.Verified, (long?)25_500), (view.Job.Status, view.Job.CostMicros));
        var evidence = view.Evidence!;
        Assert.Equal((approved.CaseId, 1, hash, "gpt-5.4-mini", 2), (evidence.CaseId, evidence.ArtifactVersion, evidence.ArtifactSha256, evidence.Model, evidence.Turns));
        Assert.Equal(new string('3', 64), evidence.DiffSha256);
        Assert.Equal("src/SampleShop.Domain/Tasks/TaskItem.cs", evidence.Changes.Single().Path);
        Assert.Equal(2, evidence.Calls.Count);
        Assert.Contains(evidence.Checks!, c => c.Name == "layers" && c.Status == CheckStatus.Passed);

        // A job whose model never edits anything ends as not finished, without a diff.
        var (second, secondHash) = await Approved(f);
        var idle = await Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, Guid.NewGuid(), Request(second.CaseId, secondHash));
        await Processor(new ScriptedModel(DeveloperScripts.Turn(new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "nothing"))))
            .ProcessNextAsync("worker", default);
        Assert.Equal((DevelopmentJobStatus.NotFinished, "no_changes"),
            ((await Durable.Api.GetDevelopmentAsync(f.Actor, f.Project, idle.JobId)).Job is var j ? (j.Status, j.FailureCode) : default));
        Assert.Equal(DevelopmentOutcome.Idle, await Processor(model).ProcessNextAsync("worker", default));
    }

    [OrchestrationFact]
    public async Task Http_SubmitsListsAndServesEvidence()
    {
        await Drain(); await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var (approved, hash) = await Approved(f);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenAI:ApiKey", "sk-test-placeholder-not-a-real-key-1234567890");
            TestSecurity.Configure(builder);
            builder.ConfigureServices(services => { services.RemoveAll<IDevelopmentJobs>(); services.AddSingleton<IDevelopmentJobs>(Durable.Api); });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestSecurity.Token(subject: f.Actor.Subject));
        HttpRequestMessage Post(string body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{f.Project}/developments") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            return request;
        }
        string Body(int version = 1, string? sha = null) =>
            $$"""{"caseId":"{{approved.CaseId}}","artifactVersion":{{version}},"artifactSha256":"{{sha ?? hash}}","templateId":"{{Template}}","templateVersion":"1.0.0","applicationName":"SampleShop"}""";
        using (var stale = await client.SendAsync(Post(Body(sha: new string('0', 64))))) Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        foreach (var invalid in new[] { "{}", Body().Replace("SampleShop", "sample shop"), Body().Replace("}", ""","extra":1}"""), Body().Replace("\"artifactVersion\":1", "\"artifactVersion\":\"1\"") })
            using (var rejected = await client.SendAsync(Post(invalid))) Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var created = await client.SendAsync(Post(Body()));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var jobId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("jobId").GetGuid();
        using (var list = await client.GetAsync($"/api/projects/{f.Project}/developments"))
            Assert.Equal("queued", JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement[0].GetProperty("status").GetString());
        using (var foreign = await client.GetAsync("/api/projects/another-project/developments")) Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        await Processor(new ScriptedModel(
            DeveloperScripts.Turn(DeveloperScripts.Write("src/SampleShop.Domain/Note.cs", "namespace SampleShop.Domain;\n", "FR-1")),
            DeveloperScripts.Turn(new DeveloperAction(DeveloperActionKind.Finish, null, null, null, [], "done")))).ProcessNextAsync("worker", default);
        using var detail = await client.GetAsync($"/api/projects/{f.Project}/developments/{jobId}");
        var root = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("verified", root.GetProperty("job").GetProperty("status").GetString());
        Assert.Equal("added", root.GetProperty("evidence").GetProperty("changes")[0].GetProperty("kind").GetString());
        using var evidence = await client.GetAsync($"/api/projects/{f.Project}/developments/{jobId}/evidence");
        var bytes = await evidence.Content.ReadAsByteArrayAsync();
        Assert.Equal($"\"sha256-{Convert.ToHexStringLower(SHA256.HashData(bytes))}\"", evidence.Headers.ETag!.Tag);
    }
}
