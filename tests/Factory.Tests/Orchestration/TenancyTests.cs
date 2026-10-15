using System.Security.Cryptography;
using Factory.Core.Authorization;
using Factory.Core.Models;
using Factory.Core.Orchestration;
using Factory.Orchestration.Durable;
using Factory.Tests.Lifecycle;
using Npgsql;

namespace Factory.Tests.Orchestration;

[Collection("Lifecycle database")]
public class TenancyTests
{
    private static Task AddProject(LifecycleFixture owner, string project)
        => owner.Sql("""
            BEGIN;
            INSERT INTO access."Projects" ("Id","IsActive","Revision") VALUES (@p,true,1);
            INSERT INTO tenancy."ProjectOwnership" ("ProjectId","OrganizationId") VALUES (@p,@o);
            INSERT INTO access."ProjectMemberships" ("ProjectId","PrincipalId","PrincipalKind","Role","IsActive","Revision") VALUES (@p,@a,'Human','Generator',true,1);
            COMMIT;
            """, new NpgsqlParameter("p", project), new NpgsqlParameter("o", owner.Organization), new NpgsqlParameter("a", owner.PrincipalId));

    private static async Task<Guid> Principal(LifecycleFixture f, string subject, PrincipalKind kind)
    {
        var id = Guid.NewGuid();
        await f.Sql("""INSERT INTO access."Principals" ("Id","Issuer","Subject","Kind","IsActive","Revision") VALUES (@i,@s,@u,@k,true,1)""",
            new NpgsqlParameter("i", id), new NpgsqlParameter("s", f.Actor.Issuer), new NpgsqlParameter("u", subject), new NpgsqlParameter("k", kind.ToString()));
        return id;
    }

    [OrchestrationFact]
    public async Task ProjectsRequireAnOwningOrganizationAndOwnershipNeverMoves()
    {
        await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        var orphan = "orphan-" + Guid.NewGuid().ToString("N");
        var missing = await Assert.ThrowsAsync<PostgresException>(() => f.Sql("""INSERT INTO access."Projects" ("Id","IsActive","Revision") VALUES (@p,true,1)""",
            new NpgsqlParameter("p", orphan)));
        Assert.Equal("23514", missing.SqlState);
        await Assert.ThrowsAsync<PostgresException>(() => f.Sql("""UPDATE tenancy."ProjectOwnership" SET "OrganizationId"=@o WHERE "ProjectId"=@p""",
            new NpgsqlParameter("o", other.Organization), new NpgsqlParameter("p", f.Project)));
        await Assert.ThrowsAsync<PostgresException>(() => f.Sql("""DELETE FROM tenancy."ProjectOwnership" WHERE "ProjectId"=@p""", new NpgsqlParameter("p", f.Project)));
        // Organization authority is human only.
        var service = await Principal(f, "svc-" + Guid.NewGuid().ToString("N"), PrincipalKind.Service);
        await Assert.ThrowsAsync<PostgresException>(() => f.Sql("""
            INSERT INTO tenancy."OrganizationMemberships" ("OrganizationId","PrincipalId","PrincipalKind","Role","IsActive","Revision") VALUES (@o,@s,'Service','Admin',true,1)
            """, new NpgsqlParameter("o", f.Organization), new NpgsqlParameter("s", service)));
        await Assert.ThrowsAsync<PostgresException>(() => f.Sql("""
            INSERT INTO tenancy."OrganizationMemberships" ("OrganizationId","PrincipalId","PrincipalKind","Role","IsActive","Revision") VALUES (@o,@s,'Human','Admin',true,1)
            """, new NpgsqlParameter("o", f.Organization), new NpgsqlParameter("s", service)));
    }

    [OrchestrationFact]
    public async Task OtherTenantsAndNonAdminProjectMembersCannotDecide()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        var run = await Durable.Submit(f); var token = RandomNumberGenerator.GetBytes(32);
        var step = (await Durable.Worker.ClaimAsync("worker", token))!;
        var decision = await Durable.Worker.RequestDecisionAsync(step.AttemptId, token, "customer_model_insufficient", ["use_factory_model", "stop"]);
        // A Human project Reviewer and Generator of this project who holds no organization role.
        var reviewerSubject = "reviewer-" + Guid.NewGuid().ToString("N");
        var reviewer = await Principal(f, reviewerSubject, PrincipalKind.Human);
        await f.Sql("""
            INSERT INTO access."ProjectMemberships" ("ProjectId","PrincipalId","PrincipalKind","Role","IsActive","Revision")
            VALUES (@p,@r,'Human','Reviewer',true,1),(@p,@r,'Human','Generator',true,1)
            """, new NpgsqlParameter("p", f.Project), new NpgsqlParameter("r", reviewer));
        var projectReviewer = new TrustedIdentity(f.Actor.Issuer, reviewerSubject, PrincipalKind.Human);
        Assert.Equal(RunStatus.Waiting, (await Durable.Api.GetRunAsync(projectReviewer, f.Project, run.RunId)).Status);
        foreach (var actor in new[] { projectReviewer, other.Actor })
            Assert.Equal(OrchestrationError.Forbidden, (await Assert.ThrowsAsync<OrchestrationException>(() =>
                Durable.Api.DecideAsync(actor, f.Project, run.RunId, decision, "use_factory_model"))).Error);
        // An Admin of the owning organization decides without needing a project role.
        await f.Sql("""
            INSERT INTO tenancy."OrganizationMemberships" ("OrganizationId","PrincipalId","PrincipalKind","Role","IsActive","Revision") VALUES (@o,@r,'Human','Admin',true,1)
            """, new NpgsqlParameter("o", f.Organization), new NpgsqlParameter("r", reviewer));
        await f.Sql("""UPDATE access."ProjectMemberships" SET "IsActive"=false,"Revision"="Revision"+1 WHERE "ProjectId"=@p AND "PrincipalId"=@r""",
            new NpgsqlParameter("p", f.Project), new NpgsqlParameter("r", reviewer));
        Assert.Equal(RunStatus.Running, (await Durable.Api.DecideAsync(projectReviewer, f.Project, run.RunId, decision, "use_factory_model")).Status);
        await Durable.Drain();
    }

    [OrchestrationFact]
    public async Task OrganizationUsageCoversOnlyItsOwnProjectsAndOnlyAdmins()
    {
        await Durable.Drain(); await using var f = await LifecycleFixture.Create(); await using var other = await LifecycleFixture.Create();
        var second = "m8-" + Guid.NewGuid().ToString("N"); await AddProject(f, second);
        var provider = "test-" + Guid.NewGuid().ToString("N");
        async Task Run(LifecycleFixture owner, string project, int input)
        {
            await Durable.Api.SubmitAsync(owner.Actor, Guid.NewGuid(), Durable.Request(owner) with { ProjectId = project });
            var models = new Durable.Provider { AnalysisUsage = new ModelUsage(provider, "m", input, 1), ArchitectureUsage = new ModelUsage(provider, "m", input, 1) };
            var processor = new DurableWorkflowProcessor(Durable.Worker, models);
            for (var i = 0; i < 3; i++) await processor.ProcessNextAsync("worker", default);
        }
        await Run(f, f.Project, 100); await Run(f, second, 10); await Run(other, other.Project, 1000);
        var now = DateTimeOffset.UtcNow;
        var usage = await Durable.Api.GetOrganizationUsageAsync(f.Actor, f.Organization, now.AddHours(-1), now.AddMinutes(1));
        Assert.Equal((4, 220L, 4L), (usage.Totals.Calls, usage.Totals.InputTokens, usage.Totals.OutputTokens));
        Assert.Equal(new[] { f.Project, second }.Order(), usage.Projects.Select(p => p.ProjectId).Order());
        Assert.Equal(200, usage.Projects.Single(p => p.ProjectId == f.Project).Totals.InputTokens);
        Assert.Equal(OrchestrationError.Forbidden, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.GetOrganizationUsageAsync(other.Actor, f.Organization, now.AddHours(-1), now))).Error);
        // A project Generator without an organization role cannot see tenant-wide spend.
        var memberSubject = "member-" + Guid.NewGuid().ToString("N"); var member = await Principal(f, memberSubject, PrincipalKind.Human);
        await f.Sql("""INSERT INTO access."ProjectMemberships" ("ProjectId","PrincipalId","PrincipalKind","Role","IsActive","Revision") VALUES (@p,@m,'Human','Generator',true,1)""",
            new NpgsqlParameter("p", f.Project), new NpgsqlParameter("m", member));
        Assert.Equal(OrchestrationError.Forbidden, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.GetOrganizationUsageAsync(new TrustedIdentity(f.Actor.Issuer, memberSubject, PrincipalKind.Human), f.Organization, now.AddHours(-1), now))).Error);
        Assert.Equal(OrchestrationError.InvalidCommand, (await Assert.ThrowsAsync<OrchestrationException>(() =>
            Durable.Api.GetOrganizationUsageAsync(f.Actor, f.Organization, now, now.AddDays(-1)))).Error);
    }

    [OrchestrationFact]
    public async Task RuntimeRolesCannotTouchTenancyDirectly()
    {
        foreach (var connection in new[] { LifecycleFixture.Runtime, Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")! })
        {
            await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
            foreach (var sql in new[] { """SELECT * FROM tenancy."OrganizationMemberships" """,
                """INSERT INTO tenancy."Organizations" VALUES ('x','x',true,1)""", """UPDATE tenancy."OrganizationMemberships" SET "IsActive"=true""",
                "SELECT tenancy.lock_admin('o','i','s','Human')", "SELECT tenancy.lock_project_admin('p','i','s','Human')" })
            {
                await using var command = new NpgsqlCommand(sql, db);
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
            }
        }
        await using var worker = new NpgsqlConnection(Environment.GetEnvironmentVariable("FACTORY_ORCHESTRATION_WORKER_CONNECTION")); await worker.OpenAsync();
        await using var usage = new NpgsqlCommand("SELECT tenancy.read_usage('o','i','s','Human',now()-interval '1 day',now())", worker);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => usage.ExecuteNonQueryAsync())).SqlState);
    }
}
