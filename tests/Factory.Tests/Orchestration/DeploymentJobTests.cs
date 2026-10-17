using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Core.Deployment;
using Factory.Core.Development;
using Factory.Core.Execution;
using Factory.Core.Orchestration;
using Factory.Core.Reviews;
using Factory.Core.Templates;
using Factory.Infrastructure.Templates;
using Factory.Orchestration.Deployment;
using Factory.Orchestration.Durable;
using Factory.Tests.Deployment;
using Factory.Tests.Integration;
using Factory.Tests.Lifecycle;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Factory.Tests.Orchestration;

[Collection("Lifecycle database")]
public class DeploymentJobTests
{
    private const string Template = "dotnet-angular-postgres-ddd";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DeploymentModule Module = new("azure-app-service-postgres", "1.1.0", "azure/app-service-postgres-1.1.0", new string('c', 64));

    private static OrchestrationError Error(Exception ex) => Assert.IsType<OrchestrationException>(ex).Error;

    // A verified development job of the fixture's project, with evidence for the starter template.
    private static async Task<DevelopmentEvidence> VerifiedJob(LifecycleFixture f)
    {
        var run = await Durable.Submit(f);
        var processor = new DurableWorkflowProcessor(Durable.Worker, new Durable.Provider());
        for (var i = 0; i < 3; i++) await processor.ProcessNextAsync("worker", default);
        var published = (await Durable.Api.GetRunAsync(f.Actor, f.Project, run.RunId)).Publication!;
        var artifact = await f.Decisions.GetArtifactAsync(f.Actor, f.Project, published.CaseId, 1);
        await f.Decisions.DecideAsync(f.Actor, f.Project, published.CaseId, new DecisionCommand(1, 1, artifact.ContentHash, ReviewDecision.Approve, null, Guid.NewGuid()));
        await Durable.Api.SubmitDevelopmentAsync(f.Actor, f.Project, Guid.NewGuid(),
            new DevelopmentRequest(published.CaseId, 1, artifact.ContentHash, Template, "1.0.0", "StockFlow"));
        var token = RandomNumberGenerator.GetBytes(32);
        ClaimedDevelopment? job;
        while ((job = await Durable.Worker.ClaimDevelopmentAsync("worker", token, 400_000, long.MaxValue / 4)) is not null && job.ProjectId != f.Project)
            await Durable.Worker.FailDevelopmentAsync(job.JobId, token, "invalid_artifact");
        var catalog = new FileSystemTemplateCatalog(Factory.Tests.Templates.TemplateRepository.Root());
        var template = catalog.Load(Template, "1.0.0").Template;
        var evidence = new DevelopmentEvidence(1, job!.JobId, f.Project, job.CaseId, job.ArtifactVersion, job.ArtifactSha256, Template, "1.0.0",
            template.ContentSha256, "StockFlow", "gpt-5.4-mini", DevelopmentJobStatus.Verified, null, 3, 12, 9_154, [],
            [new FileChange("src/StockFlow.Domain/Product.cs", FileChangeKind.Added, ["FR-1"])], new string('b', 40), new string('c', 40),
            "diff --git a/x b/x", new string('d', 64),
            new SandboxReport("sha256:" + new string('e', 64), new string('f', 64), SandboxLimits.Default, [], SandboxOutcome.Passed, null,
                DateTimeOffset.UnixEpoch, TimeSpan.Zero), ["turn 1: plan"]);
        await Durable.Worker.CompleteDevelopmentAsync(job.JobId, token, evidence);
        return evidence;
    }

    private static string NewTargetName() => "t-" + Guid.NewGuid().ToString("N")[..20];

    // Operator registration (owner login), as scripts/Register-DeploymentTarget.ps1 does it.
    private static async Task<DeploymentTarget> RegisterTarget(LifecycleFixture f, string name)
    {
        var target = DeploymentSamples.Target() with { ResourceGroup = "rg-" + name };
        await f.Sql("""
            INSERT INTO orchestration."DeploymentTargets" ("Name","ProjectId","Cloud","Environment","Application","Descriptor","CreatedAt")
            VALUES (@name,@project,'azure','staging','stock-flow',@descriptor::jsonb,now())
            """, new NpgsqlParameter("name", name), new NpgsqlParameter("project", f.Project),
            new NpgsqlParameter("descriptor", JsonSerializer.Serialize(target, Json)));
        return target;
    }

    // The queue is database-wide: end leftover deployments so each test sees only its own.
    private static async Task Drain()
    {
        await Durable.Sql("""UPDATE orchestration."Deployments" SET "LeaseExpiresAt"=now()-interval '1 second' WHERE "Status" IN ('Planning','Applying')""");
        for (var i = 0; i < 100; i++)
        {
            var token = RandomNumberGenerator.GetBytes(32);
            var claim = await Durable.Worker.ClaimDeploymentAsync("drain", token);
            if (claim is null) break;
            await Durable.Worker.FailDeploymentAsync(claim.DeploymentId, token, "test_drain", null);
        }
    }

    private static async Task<ClaimedDeployment> Claim(Guid deployment, byte[] token)
    {
        ClaimedDeployment? claim;
        while ((claim = await Durable.Worker.ClaimDeploymentAsync("worker", token)) is not null && claim.DeploymentId != deployment)
            await Durable.Worker.FailDeploymentAsync(claim.DeploymentId, token, "test_drain", null);
        return claim ?? throw new InvalidOperationException("deployment not claimed");
    }

    private static (ReleasePackage Release, DeploymentPlan Plan) Plan(DeploymentTarget target, Guid job, byte[]? planBytes = null, int destroy = 0)
    {
        var zip = Encoding.UTF8.GetBytes("release of " + job);
        var release = new ReleasePackage(job, "stock-flow", new string('c', 40), ReleasePackage.Hash(zip), zip);
        planBytes ??= Encoding.UTF8.GetBytes("plan for " + job);
        var binding = DeploymentRules.Bind(target, Module, release, planBytes);
        return (release, new DeploymentPlan(binding, binding.Hash(), planBytes,
            new PlanSummary(9, 0, destroy, [new("azurerm_linux_web_app.api", "azurerm_linux_web_app", destroy > 0 ? ["delete", "create"] : ["create"])]), ""));
    }

    [OrchestrationFact]
    public async Task Submissions_AreLimitedToTheProjectsOwnTargets_VerifiedJobs_AndOneActivePerTarget()
    {
        await Drain(); await Durable.Drain();
        await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        var evidence = await VerifiedJob(f);
        var name = NewTargetName(); await RegisterTarget(f, name);
        var foreign = NewTargetName(); await RegisterTarget(other, foreign);
        // Wrong-target denial: another project's target is not found from this project.
        Assert.Equal(OrchestrationError.NotFound, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitDeploymentAsync(f.Actor, f.Project, Guid.NewGuid(), foreign, evidence.JobId))));
        Assert.Equal(OrchestrationError.NotFound, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitDeploymentAsync(f.Actor, f.Project, Guid.NewGuid(), name, Guid.NewGuid()))));
        Assert.Equal(OrchestrationError.Forbidden, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitDeploymentAsync(other.Actor, f.Project, Guid.NewGuid(), name, evidence.JobId))));
        // Only verified jobs: a job without verified evidence is stale.
        var unfinished = await Durable.Scalar("""SELECT "Id" FROM orchestration."DevelopmentJobs" WHERE "ProjectId"=@p AND "Status"<>'Verified' LIMIT 1""",
            new NpgsqlParameter("p", f.Project));
        if (unfinished is Guid notVerified)
            Assert.Equal(OrchestrationError.StaleRevision, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
                Durable.Api.SubmitDeploymentAsync(f.Actor, f.Project, Guid.NewGuid(), name, notVerified))));

        var key = Guid.NewGuid();
        var first = await Durable.Api.SubmitDeploymentAsync(f.Actor, f.Project, key, name, evidence.JobId);
        Assert.Equal((DeploymentStatus.Queued, false), (first.Status, first.Replayed));
        Assert.True((await Durable.Api.SubmitDeploymentAsync(f.Actor, f.Project, key, name, evidence.JobId)).Replayed);
        Assert.Equal(OrchestrationError.Conflict, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitDeploymentAsync(f.Actor, f.Project, key, name, Guid.NewGuid()))));
        // One active deployment per target.
        Assert.Equal(OrchestrationError.Capacity, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.SubmitDeploymentAsync(f.Actor, f.Project, Guid.NewGuid(), name, evidence.JobId))));
        var list = await Durable.Api.ListDeploymentsAsync(f.Actor, f.Project);
        Assert.Equal(name, list.Targets.Single().Name);
        Assert.Equal(("francecentral", "rg-" + name), (list.Targets.Single().Location, list.Targets.Single().ResourceGroup));
        Assert.Equal(first.DeploymentId, list.Deployments.Single().DeploymentId);
        Assert.Equal(OrchestrationError.Forbidden, Error(await Assert.ThrowsAnyAsync<Exception>(() => Durable.Api.ListDeploymentsAsync(other.Actor, f.Project))));
        await Drain();
        // Targets never change.
        await Assert.ThrowsAsync<PostgresException>(() => Durable.Sql("""UPDATE orchestration."DeploymentTargets" SET "Application"='x' WHERE "Name"=@n""",
            new NpgsqlParameter("n", name)));
    }

    [OrchestrationFact]
    public async Task ApprovalIsBoundToTheExactPlan_AndApplyReceivesIt()
    {
        await Drain(); await Durable.Drain();
        await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        var evidence = await VerifiedJob(f);
        var name = NewTargetName(); var target = await RegisterTarget(f, name);
        var submitted = await Durable.Api.SubmitDeploymentAsync(f.Actor, f.Project, Guid.NewGuid(), name, evidence.JobId);
        var token = RandomNumberGenerator.GetBytes(32);
        var claim = await Claim(submitted.DeploymentId, token);
        Assert.Equal((DeploymentPhase.Plan, name, target, evidence.JobId), (claim.Phase, claim.TargetName, claim.Target, claim.DevelopmentJobId));
        Assert.Equal(DevelopmentEvidenceFormat.Serialize(evidence), claim.Evidence);

        var (release, plan) = Plan(target, evidence.JobId);
        // The binding must describe this job, these plan bytes and the release; the summary its destructiveness.
        var (otherRelease, otherJobPlan) = Plan(target, Guid.NewGuid());
        foreach (var (invalidRelease, invalidPlan) in new[]
        {
            (otherRelease, otherJobPlan), (release, plan with { PlanFile = Encoding.UTF8.GetBytes("other bytes") }), (otherRelease, plan),
        })
            Assert.Equal(OrchestrationError.InvalidCommand, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
                Durable.Worker.RecordPlanAsync(submitted.DeploymentId, token, Module, invalidRelease, invalidPlan))));
        Assert.Equal(OrchestrationError.Forbidden, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Worker.RecordPlanAsync(submitted.DeploymentId, RandomNumberGenerator.GetBytes(32), Module, release, plan))));
        await Durable.Worker.RecordPlanAsync(submitted.DeploymentId, token, Module, release, plan);
        var awaiting = await Durable.Api.GetDeploymentAsync(f.Actor, f.Project, submitted.DeploymentId);
        Assert.Equal((DeploymentStatus.AwaitingApproval, plan.BindingSha256, release.Sha256, false), (awaiting.Status, awaiting.BindingSha256, awaiting.ReleaseSha256, awaiting.Destructive));
        Assert.Equal(plan.Binding, awaiting.Binding);
        Assert.Equal(9, awaiting.Summary!.Add);

        // Only an Owner/Admin of the project's organization decides: a project Generator from another organization cannot.
        await f.Sql("""
            INSERT INTO access."ProjectMemberships" ("ProjectId","PrincipalId","PrincipalKind","Role","IsActive","Revision") VALUES (@p,@principal,'Human','Generator',true,1)
            """, new NpgsqlParameter("p", f.Project), new NpgsqlParameter("principal", other.PrincipalId));
        Assert.Equal(OrchestrationError.Forbidden, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.DecideDeploymentAsync(other.Actor, f.Project, submitted.DeploymentId, true, plan.BindingSha256, false))));
        // A decision on another binding is stale.
        Assert.Equal(OrchestrationError.StaleRevision, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.DecideDeploymentAsync(f.Actor, f.Project, submitted.DeploymentId, true, new string('0', 64), false))));
        var approved = await Durable.Api.DecideDeploymentAsync(f.Actor, f.Project, submitted.DeploymentId, true, plan.BindingSha256, false);
        Assert.Equal((DeploymentStatus.Approved, (bool?)true, (bool?)false), (approved.Status, approved.Approved, approved.AllowDestructive));
        Assert.Equal(OrchestrationError.StaleRevision, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.DecideDeploymentAsync(f.Actor, f.Project, submitted.DeploymentId, false, plan.BindingSha256, false))));

        var applyToken = RandomNumberGenerator.GetBytes(32);
        var apply = await Claim(submitted.DeploymentId, applyToken);
        Assert.Equal((DeploymentPhase.Apply, plan.BindingSha256, (bool?)false), (apply.Phase, apply.BindingSha256, apply.AllowDestructive));
        Assert.Equal(plan.PlanFile, apply.PlanFile);
        Assert.Equal(plan.Binding, apply.Binding);
        Assert.Equal(f.Actor.Issuer + "|" + f.Actor.Subject, apply.ApprovedBy);
        var outcome = new DeploymentOutcome(plan.BindingSha256, "https://stock-flow-staging-0a1b2c3d.azurewebsites.net",
            new Dictionary<string, string> { ["web_app_name"] = "stock-flow-staging-0a1b2c3d" }, new HealthResult("https://x/health", true, 200, 1, TimeSpan.FromSeconds(1)),
            new ConfigurationCheck(true, [new("ConnectionStrings__Database", "Resolved")]), null, apply.ApprovedBy, apply.ApprovedAt);
        Assert.Equal(OrchestrationError.InvalidCommand, Error(await Assert.ThrowsAnyAsync<Exception>(() => Durable.Worker.CompleteDeploymentAsync(
            submitted.DeploymentId, applyToken, true, outcome.Url!, outcome with { BindingSha256 = new string('1', 64) }, null))));
        Assert.Equal(OrchestrationError.InvalidCommand, Error(await Assert.ThrowsAnyAsync<Exception>(() => Durable.Worker.CompleteDeploymentAsync(
            submitted.DeploymentId, applyToken, true, "http://insecure.example", outcome, null))));
        await Durable.Worker.CompleteDeploymentAsync(submitted.DeploymentId, applyToken, true, outcome.Url!, outcome, null);
        var done = await Durable.Api.GetDeploymentAsync(f.Actor, f.Project, submitted.DeploymentId);
        Assert.Equal((DeploymentStatus.Succeeded, outcome.Url), (done.Status, done.Url));
        Assert.Equal("Resolved", done.Result!.Configuration!.References.Single().Status);
        foreach (var sql in new[]
        {
            """UPDATE orchestration."Deployments" SET "Status"='Failed',"FailureCode"='x' WHERE "Id"=@id""",
            """UPDATE orchestration."Deployments" SET "Url"='https://evil.example' WHERE "Id"=@id""",
            """DELETE FROM orchestration."Deployments" WHERE "Id"=@id""",
        })
            await Assert.ThrowsAsync<PostgresException>(() => Durable.Sql(sql, new NpgsqlParameter("id", submitted.DeploymentId)));
    }

    [OrchestrationFact]
    public async Task DestructivePlans_NeedAnExplicitApproval_StalePlansExpire_AndLostWorkersEndTheDeployment()
    {
        await Drain(); await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var evidence = await VerifiedJob(f);
        var name = NewTargetName(); var target = await RegisterTarget(f, name);

        async Task<(Guid Id, DeploymentPlan Plan)> Planned(int destroy)
        {
            var submitted = await Durable.Api.SubmitDeploymentAsync(f.Actor, f.Project, Guid.NewGuid(), name, evidence.JobId);
            var token = RandomNumberGenerator.GetBytes(32);
            await Claim(submitted.DeploymentId, token);
            var (release, plan) = Plan(target, evidence.JobId, Encoding.UTF8.GetBytes("plan " + Guid.NewGuid()), destroy);
            await Durable.Worker.RecordPlanAsync(submitted.DeploymentId, token, Module, release, plan);
            return (submitted.DeploymentId, plan);
        }

        var destructive = await Planned(destroy: 1);
        Assert.True((await Durable.Api.GetDeploymentAsync(f.Actor, f.Project, destructive.Id)).Destructive);
        Assert.Equal(OrchestrationError.InvalidCommand, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.DecideDeploymentAsync(f.Actor, f.Project, destructive.Id, true, destructive.Plan.BindingSha256, false))));
        var rejected = await Durable.Api.DecideDeploymentAsync(f.Actor, f.Project, destructive.Id, false, destructive.Plan.BindingSha256, false);
        Assert.Equal((DeploymentStatus.Rejected, (bool?)false), (rejected.Status, rejected.Approved));

        // An approval is valid for 24 hours after planning.
        var stale = await Planned(destroy: 0);
        // Simulated clock: the transition guard (rightly) refuses edits of a planned row, so it is paused for this one update.
        await Durable.Sql("""
            BEGIN;
            ALTER TABLE orchestration."Deployments" DISABLE TRIGGER deployment_transition;
            UPDATE orchestration."Deployments" SET "PlannedAt"=now()-interval '25 hours' WHERE "Id"=@id;
            ALTER TABLE orchestration."Deployments" ENABLE TRIGGER deployment_transition;
            COMMIT;
            """, new NpgsqlParameter("id", stale.Id));
        Assert.Equal(OrchestrationError.StaleRevision, Error(await Assert.ThrowsAnyAsync<Exception>(() =>
            Durable.Api.DecideDeploymentAsync(f.Actor, f.Project, stale.Id, true, stale.Plan.BindingSha256, false))));
        Assert.Equal((DeploymentStatus.Expired, "plan_expired"),
            ((await Durable.Api.GetDeploymentAsync(f.Actor, f.Project, stale.Id)) is var s ? (s.Status, s.FailureCode) : default));

        // A worker lost while applying ends the deployment; nothing is applied again without a new plan.
        var lost = await Planned(destroy: 0);
        await Durable.Api.DecideDeploymentAsync(f.Actor, f.Project, lost.Id, true, lost.Plan.BindingSha256, false);
        var token = RandomNumberGenerator.GetBytes(32);
        await Claim(lost.Id, token);
        await Durable.Sql("""UPDATE orchestration."Deployments" SET "LeaseExpiresAt"=now()-interval '1 second' WHERE "Id"=@id""", new NpgsqlParameter("id", lost.Id));
        var nextToken = RandomNumberGenerator.GetBytes(32);
        if (await Durable.Worker.ClaimDeploymentAsync("worker", nextToken) is { } next) await Durable.Worker.FailDeploymentAsync(next.DeploymentId, nextToken, "test_drain", null);
        Assert.Equal((DeploymentStatus.Failed, "worker_lost"),
            ((await Durable.Api.GetDeploymentAsync(f.Actor, f.Project, lost.Id)) is var l ? (l.Status, l.FailureCode) : default));
        Assert.Equal(OrchestrationError.LeaseLost, Error(await Assert.ThrowsAnyAsync<Exception>(() => Durable.Worker.RenewDeploymentAsync(lost.Id, token))));
        await Drain();
    }

    [OrchestrationFact]
    public async Task RuntimeAndWorkerRoles_ReachOnlyTheirOwnDeploymentFunctions()
    {
        var worker = Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")!;
        foreach (var (connection, sql) in new[]
        {
            (worker, "SELECT orchestration.submit_deployment('p','i','s','Human',gen_random_uuid(),'t',gen_random_uuid())"),
            (worker, "SELECT orchestration.decide_deployment('p','i','s','Human',gen_random_uuid(),true,repeat('a',64),false)"),
            (worker, """SELECT count(*) FROM orchestration."Deployments" """),
            (worker, """INSERT INTO orchestration."DeploymentTargets" ("Name","ProjectId","Cloud","Environment","Application","Descriptor","CreatedAt") VALUES ('x','p','azure','staging','abc','{}',now())"""),
            (LifecycleFixture.Runtime, "SELECT orchestration.claim_deployment('w',decode(repeat('00',32),'hex'))"),
            (LifecycleFixture.Runtime, "SELECT orchestration.expire_deployments()"),
            (LifecycleFixture.Runtime, """SELECT count(*) FROM orchestration."DeploymentTargets" """),
        })
        {
            await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
            await using var command = new NpgsqlCommand(sql, db);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync())).SqlState);
        }
    }

    private sealed class FakeBuilder(Func<int, byte[]> zip) : IReleaseBuilder
    {
        private int _builds;
        public Task<ReleasePackage> BuildAsync(Guid job, DevelopmentEvidence evidence, IReadOnlyList<TemplateFile> baseFiles, CancellationToken ct = default)
        {
            var bytes = zip(_builds++);
            return Task.FromResult(new ReleasePackage(job, ApplicationName.Create(evidence.ApplicationName).Kebab, evidence.FinalCommit!, ReleasePackage.Hash(bytes), bytes));
        }
    }

    private sealed class FakeEngine : IDeploymentEngine
    {
        public List<string> Calls { get; } = [];
        public Task<DeploymentPlan> PlanAsync(DeploymentTarget target, DeploymentModule module, ReleasePackage release, CancellationToken ct = default)
        {
            Calls.Add("plan");
            var bytes = Encoding.UTF8.GetBytes("plan " + release.Sha256);
            var binding = DeploymentRules.Bind(target, module, release, bytes);
            return Task.FromResult(new DeploymentPlan(binding, binding.Hash(), bytes, new PlanSummary(2, 5, 0, []), "log"));
        }

        public Task<DeploymentResult> ApplyAsync(DeploymentTarget target, DeploymentModule module, ReleasePackage release, DeploymentPlan plan,
            DeploymentApproval approval, CancellationToken ct = default)
        {
            DeploymentRules.Verify(target, module, release, plan, approval);
            Calls.Add("apply");
            return Task.FromResult(new DeploymentResult(plan.BindingSha256, "https://stock-flow-staging-0a1b2c3d.azurewebsites.net",
                new Dictionary<string, string> { ["url"] = "https://stock-flow-staging-0a1b2c3d.azurewebsites.net", ["web_app_name"] = "stock-flow-staging-0a1b2c3d" }, "log"));
        }
    }

    private sealed class FakeChecks(bool resolved) : IHealthChecker, IConfigurationChecker
    {
        public Task<HealthResult> CheckAsync(string baseUrl, CancellationToken ct = default) => Task.FromResult(new HealthResult(baseUrl + "/health", true, 200, 1, TimeSpan.Zero));
        public Task<ConfigurationCheck> CheckAsync(DeploymentTarget target, IReadOnlyDictionary<string, string> outputs, CancellationToken ct = default)
            => Task.FromResult(new ConfigurationCheck(resolved, [new("ConnectionStrings__Database", resolved ? "Resolved" : "AccessToKeyVaultDenied")]));
    }

    private static DeploymentProcessor Processor(IReleaseBuilder builder, FakeEngine engine, bool resolved = true)
    {
        var root = DeploymentSamples.RepositoryRoot();
        var checks = new FakeChecks(resolved);
        return new DeploymentProcessor(Durable.Worker, new FileSystemTemplateCatalog(Path.Combine(root, "templates")),
            new Factory.Infrastructure.Deployment.FileSystemModuleCatalog(Path.Combine(root, "deploy")), builder, engine, checks, checks,
            "azure-app-service-postgres", "1.1.0");
    }

    [OrchestrationFact]
    public async Task Processor_PlansAndAppliesTheApprovedPlan_AndVerifiesConfiguration()
    {
        await Drain(); await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var evidence = await VerifiedJob(f);
        var name = NewTargetName(); await RegisterTarget(f, name);
        var engine = new FakeEngine();
        var processor = Processor(new FakeBuilder(_ => [1, 2, 3]), engine);
        var submitted = await Durable.Api.SubmitDeploymentAsync(f.Actor, f.Project, Guid.NewGuid(), name, evidence.JobId);
        Assert.Equal(DeploymentRunOutcome.Planned, await processor.ProcessNextAsync("worker", default));
        var planned = await Durable.Api.GetDeploymentAsync(f.Actor, f.Project, submitted.DeploymentId);
        Assert.Equal((DeploymentStatus.AwaitingApproval, "1.1.0"), (planned.Status, planned.ModuleVersion));
        // Nothing is applied before a human approves.
        Assert.Equal(DeploymentRunOutcome.Idle, await processor.ProcessNextAsync("worker", default));
        await Durable.Api.DecideDeploymentAsync(f.Actor, f.Project, submitted.DeploymentId, true, planned.BindingSha256!, false);
        Assert.Equal(DeploymentRunOutcome.Applied, await processor.ProcessNextAsync("worker", default));
        Assert.Equal(["plan", "apply"], engine.Calls);
        var done = await Durable.Api.GetDeploymentAsync(f.Actor, f.Project, submitted.DeploymentId);
        Assert.Equal((DeploymentStatus.Succeeded, "https://stock-flow-staging-0a1b2c3d.azurewebsites.net"), (done.Status, done.Url));

        // Unresolved secret references make a healthy deployment Unhealthy (the M13A.1 finding).
        var unresolved = Processor(new FakeBuilder(_ => [1, 2, 3]), new FakeEngine(), resolved: false);
        var second = await Durable.Api.SubmitDeploymentAsync(f.Actor, f.Project, Guid.NewGuid(), name, evidence.JobId);
        await unresolved.ProcessNextAsync("worker", default);
        var secondPlan = await Durable.Api.GetDeploymentAsync(f.Actor, f.Project, second.DeploymentId);
        await Durable.Api.DecideDeploymentAsync(f.Actor, f.Project, second.DeploymentId, true, secondPlan.BindingSha256!, false);
        await unresolved.ProcessNextAsync("worker", default);
        Assert.Equal((DeploymentStatus.Unhealthy, "configuration_unresolved"),
            ((await Durable.Api.GetDeploymentAsync(f.Actor, f.Project, second.DeploymentId)) is var u ? (u.Status, u.FailureCode) : default));

        // A rebuild that does not reproduce the planned release is refused before apply.
        var drifting = new FakeEngine();
        var changing = Processor(new FakeBuilder(build => [(byte)build]), drifting);
        var third = await Durable.Api.SubmitDeploymentAsync(f.Actor, f.Project, Guid.NewGuid(), name, evidence.JobId);
        await changing.ProcessNextAsync("worker", default);
        var thirdPlan = await Durable.Api.GetDeploymentAsync(f.Actor, f.Project, third.DeploymentId);
        await Durable.Api.DecideDeploymentAsync(f.Actor, f.Project, third.DeploymentId, true, thirdPlan.BindingSha256!, false);
        Assert.Equal(DeploymentRunOutcome.Failed, await changing.ProcessNextAsync("worker", default));
        Assert.Equal(["plan"], drifting.Calls);
        Assert.Equal((DeploymentStatus.Failed, "release_mismatch"),
            ((await Durable.Api.GetDeploymentAsync(f.Actor, f.Project, third.DeploymentId)) is var t ? (t.Status, t.FailureCode) : default));
    }

    [OrchestrationFact]
    public async Task Http_ListsSubmitsAndDecides()
    {
        await Drain(); await Durable.Drain(); await using var f = await LifecycleFixture.Create();
        var evidence = await VerifiedJob(f);
        var name = NewTargetName(); await RegisterTarget(f, name);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenAI:ApiKey", "sk-test-placeholder-not-a-real-key-1234567890");
            TestSecurity.Configure(builder);
            builder.ConfigureServices(services => { services.RemoveAll<IDeploymentJobs>(); services.AddSingleton<IDeploymentJobs>(Durable.Api); });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestSecurity.Token(subject: f.Actor.Subject));
        HttpRequestMessage Post(string path, string body, bool key = true)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (key) request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            return request;
        }
        var root = $"/api/projects/{f.Project}/deployments";
        foreach (var invalid in new[] { "{}", $$"""{"targetName":"Bad Name","developmentJobId":"{{evidence.JobId}}"}""",
            $$"""{"targetName":"{{name}}","developmentJobId":"{{evidence.JobId}}","extra":1}""" })
            using (var rejected = await client.SendAsync(Post(root, invalid))) Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using (var noKey = await client.SendAsync(Post(root, $$"""{"targetName":"{{name}}","developmentJobId":"{{evidence.JobId}}"}""", key: false)))
            Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        using var created = await client.SendAsync(Post(root, $$"""{"targetName":"{{name}}","developmentJobId":"{{evidence.JobId}}"}"""));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("deploymentId").GetGuid();
        using (var list = await client.GetAsync(root))
        {
            var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(name, listed.GetProperty("targets")[0].GetProperty("name").GetString());
            Assert.Equal("queued", listed.GetProperty("deployments")[0].GetProperty("status").GetString());
        }
        await Processor(new FakeBuilder(_ => [7]), new FakeEngine()).ProcessNextAsync("worker", default);
        string binding;
        using (var detail = await client.GetAsync($"{root}/{id}"))
        {
            var view = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("awaitingApproval", view.GetProperty("status").GetString());
            binding = view.GetProperty("bindingSha256").GetString()!;
        }
        using (var bad = await client.SendAsync(Post($"{root}/{id}/decision", """{"approve":true,"bindingSha256":"short","allowDestructive":false}""", key: false)))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using (var stale = await client.SendAsync(Post($"{root}/{id}/decision", $$"""{"approve":true,"bindingSha256":"{{new string('0', 64)}}","allowDestructive":false}""", key: false)))
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var approved = await client.SendAsync(Post($"{root}/{id}/decision", $$"""{"approve":true,"bindingSha256":"{{binding}}","allowDestructive":false}""", key: false));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("approved", JsonDocument.Parse(await approved.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
        await Drain();
    }
}
