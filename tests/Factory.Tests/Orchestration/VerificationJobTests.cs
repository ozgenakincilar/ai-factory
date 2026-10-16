using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Core.Authorization;
using Factory.Core.Execution;
using Factory.Core.Orchestration;
using Factory.Core.Templates;
using Factory.Infrastructure.Templates;
using Factory.Orchestration.Verification;
using Factory.Tests.Integration;
using Factory.Tests.Lifecycle;
using Factory.Tests.Templates;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Factory.Tests.Orchestration;

[Collection("Lifecycle database")]
public class VerificationJobTests
{
    private const string Template = "dotnet-angular-postgres-ddd";
    private static readonly VerificationRequest Request = new(Template, "1.0.0", "SampleShop");
    private static readonly string Hash = new('a', 64);

    // The queue is database-wide: end leftover jobs so each test sees only its own.
    private static async Task Drain()
    {
        await Durable.Sql("""UPDATE orchestration."VerificationJobs" SET "LeaseExpiresAt"=now()-interval '1 second' WHERE "Status"='Running'""");
        for (var i = 0; i < 200; i++)
        {
            var token = RandomNumberGenerator.GetBytes(32);
            var job = await Durable.Worker.ClaimVerificationAsync("drain", token);
            if (job is null)
            {
                if (Convert.ToInt64(await Durable.Scalar("""SELECT count(*) FROM orchestration."VerificationJobs" WHERE "Status" IN ('Queued','Running')""")) == 0) return;
                await Durable.Sql("""UPDATE orchestration."VerificationJobs" SET "LeaseExpiresAt"=now()-interval '1 second' WHERE "Status"='Running'""");
                continue;
            }
            await Durable.Worker.FailVerificationAsync(job.JobId, token, "template_unavailable");
        }
    }

    private static SandboxReport Report(SandboxOutcome outcome = SandboxOutcome.Passed) => new("sha256:" + new string('b', 64), Hash,
        SandboxLimits.Default, [new("dotnet-test", outcome == SandboxOutcome.Passed ? StepOutcome.Passed : StepOutcome.Failed,
            outcome == SandboxOutcome.Passed ? 0 : 1, TimeSpan.FromSeconds(3), "Passed!", false)],
        outcome, outcome == SandboxOutcome.Passed ? null : "step_failed", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(4));

    private static VerificationEvidence Evidence(ClaimedVerification job, SandboxOutcome outcome = SandboxOutcome.Passed, string? project = null)
        => new(1, job.JobId, project ?? job.ProjectId, job.TemplateId, job.TemplateVersion, Hash, job.ApplicationName, Hash, Report(outcome));

    private static OrchestrationError Error(Exception ex) => Assert.IsType<OrchestrationException>(ex).Error;

    [OrchestrationFact]
    public async Task Submission_IsIdempotent_Bounded_AndAuthorizedPerProject()
    {
        await Drain(); await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        var key = Guid.NewGuid();
        var first = await Durable.Api.SubmitVerificationAsync(f.Actor, f.Project, key, Request);
        Assert.Equal((VerificationStatus.Queued, false), (first.Status, first.Replayed));
        var replay = await Durable.Api.SubmitVerificationAsync(f.Actor, f.Project, key, Request);
        Assert.Equal((first.JobId, true), (replay.JobId, replay.Replayed));
        Assert.Equal(OrchestrationError.Conflict, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitVerificationAsync(f.Actor, f.Project, key, Request with { ApplicationName = "OtherShop" }))));
        Assert.Equal(OrchestrationError.InvalidCommand, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitVerificationAsync(f.Actor, f.Project, Guid.NewGuid(), Request with { ApplicationName = "bad-name" }))));
        Assert.Equal(OrchestrationError.Forbidden, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitVerificationAsync(other.Actor, f.Project, Guid.NewGuid(), Request))));
        Assert.Equal(OrchestrationError.Forbidden, Error(await Assert.ThrowsAnyAsync<Exception>(() => Durable.Api.ListVerificationsAsync(other.Actor, f.Project))));
        Assert.Equal(OrchestrationError.Forbidden, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.GetVerificationAsync(other.Actor, f.Project, first.JobId))));
        Assert.Equal(OrchestrationError.NotFound, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.GetVerificationAsync(other.Actor, other.Project, first.JobId))));
        Assert.Empty(await Durable.Api.ListVerificationsAsync(other.Actor, other.Project));
        for (var i = 0; i < VerificationLimits.ActiveJobsPerProject - 1; i++)
            await Durable.Api.SubmitVerificationAsync(f.Actor, f.Project, Guid.NewGuid(), Request);
        Assert.Equal(OrchestrationError.Capacity, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitVerificationAsync(f.Actor, f.Project, Guid.NewGuid(), Request))));
        var listed = await Durable.Api.ListVerificationsAsync(f.Actor, f.Project);
        Assert.Equal(VerificationLimits.ActiveJobsPerProject, listed.Count);
        Assert.Equal(first.JobId, listed[^1].JobId);
        Assert.All(listed, j => Assert.Equal(("SampleShop", VerificationStatus.Queued, 0), (j.ApplicationName, j.Status, j.Attempts)));
        await Drain();
    }

    [OrchestrationFact]
    public async Task Completion_StoresImmutableHashedEvidence_ForThatJobOnly()
    {
        await Drain(); await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        var submitted = await Durable.Api.SubmitVerificationAsync(f.Actor, f.Project, Guid.NewGuid(), Request);
        var token = RandomNumberGenerator.GetBytes(32);
        var job = (await Durable.Worker.ClaimVerificationAsync("worker", token))!;
        Assert.Equal((submitted.JobId, f.Project, Template, "1.0.0", "SampleShop", 1),
            (job.JobId, job.ProjectId, job.TemplateId, job.TemplateVersion, job.ApplicationName, job.Attempt));
        Assert.Equal(VerificationStatus.Running, (await Durable.Api.GetVerificationAsync(f.Actor, f.Project, job.JobId)).Job.Status);

        // The envelope must describe this job, and only the lease owner may complete it.
        Assert.Equal(OrchestrationError.InvalidCommand, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Worker.CompleteVerificationAsync(job.JobId, token, Evidence(job, project: other.Project)))));
        Assert.Equal(OrchestrationError.InvalidCommand, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Worker.CompleteVerificationAsync(job.JobId, token, Evidence(job) with { ManifestSha256 = new string('c', 64) }))));
        Assert.Equal(OrchestrationError.Forbidden, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Worker.CompleteVerificationAsync(job.JobId, RandomNumberGenerator.GetBytes(32), Evidence(job)))));

        await Durable.Worker.CompleteVerificationAsync(job.JobId, token, Evidence(job));
        var view = await Durable.Api.GetVerificationAsync(f.Actor, f.Project, job.JobId);
        Assert.Equal((VerificationStatus.Passed, null), (view.Job.Status, view.Job.FailureCode));
        var bytes = await Durable.Api.GetVerificationEvidenceAsync(f.Actor, f.Project, job.JobId);
        Assert.Equal(VerificationEvidenceFormat.Serialize(Evidence(job)), bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), view.EvidenceSha256);
        Assert.Equal(StepOutcome.Passed, view.Evidence!.Report.Steps.Single().Outcome);
        Assert.Equal(OrchestrationError.Forbidden, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.GetVerificationEvidenceAsync(other.Actor, f.Project, job.JobId))));

        // Finished jobs and their evidence never change, even for the owner login.
        Assert.Equal(OrchestrationError.LeaseLost, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Worker.CompleteVerificationAsync(job.JobId, token, Evidence(job)))));
        foreach (var sql in new[]
        {
            """UPDATE orchestration."VerificationEvidence" SET "Outcome"='Failed'""",
            """DELETE FROM orchestration."VerificationEvidence" """,
            """UPDATE orchestration."VerificationJobs" SET "Status"='Failed',"FailureCode"='x' WHERE "Status"='Passed'""",
            """DELETE FROM orchestration."VerificationJobs" """,
        })
            await Assert.ThrowsAsync<PostgresException>(() => Durable.Sql(sql));

        // A failed report finishes the job as failed with the report's code.
        var failed = await Durable.Api.SubmitVerificationAsync(f.Actor, f.Project, Guid.NewGuid(), Request);
        var failedToken = RandomNumberGenerator.GetBytes(32);
        var failedJob = (await Durable.Worker.ClaimVerificationAsync("worker", failedToken))!;
        Assert.Equal(failed.JobId, failedJob.JobId);
        await Durable.Worker.CompleteVerificationAsync(failedJob.JobId, failedToken, Evidence(failedJob, SandboxOutcome.Failed));
        Assert.Equal((VerificationStatus.Failed, "step_failed"),
            ((await Durable.Api.GetVerificationAsync(f.Actor, f.Project, failed.JobId)).Job.Status,
             (await Durable.Api.GetVerificationAsync(f.Actor, f.Project, failed.JobId)).Job.FailureCode));
        await Drain();
    }

    [OrchestrationFact]
    public async Task ALostLease_IsRetriedOnce_ThenTheJobFails()
    {
        await Drain(); await using var f = await LifecycleFixture.Create();
        var submitted = await Durable.Api.SubmitVerificationAsync(f.Actor, f.Project, Guid.NewGuid(), Request);
        var oldToken = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(1, (await Durable.Worker.ClaimVerificationAsync("worker-a", oldToken))!.Attempt);
        async Task Expire() => await Durable.Sql("""UPDATE orchestration."VerificationJobs" SET "LeaseExpiresAt"=now()-interval '1 second' WHERE "Id"=@id""",
            new NpgsqlParameter("id", submitted.JobId));
        await Expire();
        var newToken = RandomNumberGenerator.GetBytes(32);
        var retried = (await Durable.Worker.ClaimVerificationAsync("worker-b", newToken))!;
        Assert.Equal((submitted.JobId, 2), (retried.JobId, retried.Attempt));
        // While another worker holds the job, the old token is refused like any foreign token (as for workflow attempts).
        Assert.Equal(OrchestrationError.Forbidden, Error(await Assert.ThrowsAnyAsync<Exception>(() => Durable.Worker.RenewVerificationAsync(submitted.JobId, oldToken))));
        await Durable.Worker.RenewVerificationAsync(submitted.JobId, newToken);
        await Expire();
        Assert.Null(await Durable.Worker.ClaimVerificationAsync("worker-c", RandomNumberGenerator.GetBytes(32)));
        var job = (await Durable.Api.GetVerificationAsync(f.Actor, f.Project, submitted.JobId)).Job;
        Assert.Equal((VerificationStatus.Failed, "attempts_exhausted", 2), (job.Status, job.FailureCode, job.Attempts));
        Assert.Equal(OrchestrationError.LeaseLost, Error(await Assert.ThrowsAnyAsync<Exception>(() => Durable.Worker.RenewVerificationAsync(submitted.JobId, newToken))));
    }

    [OrchestrationFact]
    public async Task RuntimeAndWorkerRoles_ReachOnlyTheirOwnFunctions()
    {
        foreach (var (connection, sql) in new[]
        {
            (Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")!,
                "SELECT orchestration.submit_verification('p','i','s','Human',gen_random_uuid(),'t','1','SampleShop')"),
            (Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")!, "SELECT orchestration.list_verifications('p','i','s','Human')"),
            (Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")!, """SELECT count(*) FROM orchestration."VerificationEvidence" """),
            (LifecycleFixture.Runtime, "SELECT orchestration.claim_verification('w',decode(repeat('00',32),'hex'))"),
            (LifecycleFixture.Runtime, "SELECT orchestration.complete_verification(gen_random_uuid(),decode(repeat('00',32),'hex'),'\\x7b7d')"),
            (LifecycleFixture.Runtime, """SELECT count(*) FROM orchestration."VerificationJobs" """),
        })
        {
            await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
            await using var command = new NpgsqlCommand(sql, db);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync())).SqlState);
        }
    }

    private sealed class FakeSandbox(SandboxOutcome outcome) : ISandbox
    {
        public IReadOnlyList<TemplateFile>? Workspace;
        public IReadOnlyList<SandboxStep>? Steps;
        public Task<SandboxReport> RunAsync(IReadOnlyList<TemplateFile> workspace, IReadOnlyList<SandboxStep> steps, CancellationToken ct = default)
        {
            Workspace = workspace; Steps = steps;
            return Task.FromResult(Report(outcome) with { WorkspaceSha256 = TemplateContent.Hash(TemplateContent.Normalize(workspace)) });
        }
    }

    [OrchestrationFact]
    public async Task Processor_GeneratesVerifies_AndRecordsEvidence_OrFailsUnknownTemplates()
    {
        await Drain(); await using var f = await LifecycleFixture.Create();
        var catalog = new FileSystemTemplateCatalog(TemplateRepository.Root());
        var sandbox = new FakeSandbox(SandboxOutcome.Passed);
        var processor = new VerificationProcessor(Durable.Worker, catalog, sandbox);
        var job = await Durable.Api.SubmitVerificationAsync(f.Actor, f.Project, Guid.NewGuid(), Request);
        Assert.Equal(VerificationOutcome.Completed, await processor.ProcessNextAsync("worker", default));
        Assert.Contains(sandbox.Workspace!, file => file.Path == "SampleShop.slnx");
        Assert.Equal("dotnet-restore", sandbox.Steps![0].Name);
        var view = await Durable.Api.GetVerificationAsync(f.Actor, f.Project, job.JobId);
        var (template, files) = catalog.Load(Template, "1.0.0");
        var expected = StarterGenerator.Generate(template, files, ApplicationName.Create("SampleShop"));
        Assert.Equal((VerificationStatus.Passed, template.ContentSha256, expected.ManifestSha256),
            (view.Job.Status, view.Evidence!.TemplateSha256, view.Evidence.ManifestSha256));

        var unknown = await Durable.Api.SubmitVerificationAsync(f.Actor, f.Project, Guid.NewGuid(), Request with { TemplateVersion = "9.9.9" });
        Assert.Equal(VerificationOutcome.Failed, await processor.ProcessNextAsync("worker", default));
        var failed = (await Durable.Api.GetVerificationAsync(f.Actor, f.Project, unknown.JobId));
        Assert.Equal((VerificationStatus.Failed, "template_unavailable", (VerificationEvidence?)null), (failed.Job.Status, failed.Job.FailureCode, failed.Evidence));
        Assert.Equal(VerificationOutcome.Idle, await processor.ProcessNextAsync("worker", default));
    }

    [OrchestrationFact]
    public async Task Http_SubmitsListsAndServesEvidence()
    {
        await Drain(); await using var f = await LifecycleFixture.Create();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenAI:ApiKey", "sk-test-placeholder-not-a-real-key-1234567890");
            TestSecurity.Configure(builder);
            builder.ConfigureServices(services => { services.RemoveAll<IVerificationJobs>(); services.AddSingleton<IVerificationJobs>(Durable.Api); });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestSecurity.Token(subject: f.Actor.Subject));
        HttpRequestMessage Post(string body, Guid? key = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{f.Project}/verifications")
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString("D"));
            return request;
        }
        const string body = """{"templateId":"dotnet-angular-postgres-ddd","templateVersion":"1.0.0","applicationName":"SampleShop"}""";
        var key = Guid.NewGuid();
        using var created = await client.SendAsync(Post(body, key));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var jobId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("jobId").GetGuid();
        Assert.Equal($"/api/projects/{f.Project}/verifications/{jobId:D}", created.Headers.Location!.ToString());
        using (var replayed = await client.SendAsync(Post(body, key))) Assert.Equal("true", replayed.Headers.GetValues("Idempotency-Replayed").Single());
        foreach (var invalid in new[] { """{"templateId":"x","templateVersion":"1"}""", body.Replace("SampleShop", "sample shop"),
            body.Replace("}", ""","extra":1}"""), "[]" })
            using (var rejected = await client.SendAsync(Post(invalid))) Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using (var noKey = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{f.Project}/verifications")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") })
        using (var rejected = await client.SendAsync(noKey)) Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        using (var list = await client.GetAsync($"/api/projects/{f.Project}/verifications"))
            Assert.Equal("queued", JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement[0].GetProperty("status").GetString());
        using (var missing = await client.GetAsync($"/api/projects/{f.Project}/verifications/{jobId}/evidence"))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var foreign = await client.GetAsync("/api/projects/another-project/verifications")) Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

        await new VerificationProcessor(Durable.Worker, new FileSystemTemplateCatalog(TemplateRepository.Root()), new FakeSandbox(SandboxOutcome.Passed))
            .ProcessNextAsync("worker", default);
        using var detail = await client.GetAsync($"/api/projects/{f.Project}/verifications/{jobId}");
        var root = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("passed", root.GetProperty("job").GetProperty("status").GetString());
        Assert.Equal("passed", root.GetProperty("evidence").GetProperty("report").GetProperty("outcome").GetString());
        using var evidence = await client.GetAsync($"/api/projects/{f.Project}/verifications/{jobId}/evidence");
        var bytes = await evidence.Content.ReadAsByteArrayAsync();
        Assert.Equal($"\"sha256-{Convert.ToHexStringLower(SHA256.HashData(bytes))}\"", evidence.Headers.ETag!.Tag);
        Assert.Equal(root.GetProperty("evidenceSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        await Drain();
    }
}
