using System.Security.Cryptography;
using System.Text.Json;
using Factory.Core.Authorization;
using Factory.Core.Lifecycle;
using Factory.Core.Reviews;
using Factory.Infrastructure.Authorization;
using Factory.Infrastructure.Lifecycle;
using Factory.Infrastructure.Reviews;
using Factory.Tests.Integration;
using Factory.Tests.Reviews;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Factory.Tests.Lifecycle;

public sealed class LifecycleFactAttribute : FactAttribute
{
    public LifecycleFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_LIFECYCLE_TEST_CONNECTION")))
            Skip = "Requires isolated migrated M6 PostgreSQL and restricted lifecycle runtime.";
    }
}
public sealed class LifecycleTheoryAttribute : TheoryAttribute
{
    public LifecycleTheoryAttribute() { Skip = new LifecycleFactAttribute().Skip; }
}
[CollectionDefinition("Lifecycle database", DisableParallelization = true)]
public class LifecycleDatabaseCollection { }

internal sealed class LifecycleFixture : IAsyncDisposable
{
    internal static string Owner => Environment.GetEnvironmentVariable("FACTORY_LIFECYCLE_OWNER_CONNECTION")!;
    internal static string Runtime => Environment.GetEnvironmentVariable("FACTORY_LIFECYCLE_TEST_CONNECTION")!;
    internal static DbContextOptions<ReviewDbContext> Options(string connection) => new DbContextOptionsBuilder<ReviewDbContext>().UseNpgsql(connection).Options;
    internal PostgresLifecycleStore Store { get; } = new(Runtime);
    internal string Project { get; } = "m6-" + Guid.NewGuid().ToString("N");
    internal TrustedIdentity Actor { get; private set; } = null!;
    internal Guid PrincipalId { get; private set; }
    internal byte[] Token { get; } = RandomNumberGenerator.GetBytes(32);
    internal GenerationCommand Command => new(Guid.NewGuid(), null, null, LifecycleLimits.WorkflowVersion, ReviewSamples.Request with { ProjectId = Project });
    internal static async Task<LifecycleFixture> Create(PrincipalKind kind = PrincipalKind.Human, string? issuer = null, string? subject = null)
    {
        var fixture = new LifecycleFixture { PrincipalId = Guid.NewGuid(), Actor = new(issuer ?? TestSecurity.Issuer, subject ?? Guid.NewGuid().ToString("N"), kind) };
        await using var db = new ReviewDbContext(Options(Owner));
        var existing = await db.Set<PrincipalRow>().SingleOrDefaultAsync(p => p.Issuer == fixture.Actor.Issuer && p.Subject == fixture.Actor.Subject);
        if (existing is not null) { Assert.Equal(kind, existing.Kind); Assert.True(existing.IsActive); fixture.PrincipalId = existing.Id; }
        db.Add(new ProjectRow { Id = fixture.Project, IsActive = true });
        if (existing is null) db.Add(new PrincipalRow { Id = fixture.PrincipalId, Issuer = fixture.Actor.Issuer, Subject = fixture.Actor.Subject, Kind = kind, IsActive = true });
        db.Add(new ProjectMembershipRow { ProjectId = fixture.Project, PrincipalId = fixture.PrincipalId, PrincipalKind = kind, Role = ProjectRole.Generator, IsActive = true });
        if (kind == PrincipalKind.Human) db.Add(new ProjectMembershipRow { ProjectId = fixture.Project, PrincipalId = fixture.PrincipalId, PrincipalKind = kind, Role = ProjectRole.Reviewer, IsActive = true });
        await db.SaveChangesAsync(); return fixture;
    }
    internal async Task<(GenerationCommand Command, GenerationAdmission Admission, GenerationOutcome Published)> Publish()
    {
        var command = Command; var admission = await Store.AdmitAsync(Actor, command, Token);
        var published = await Store.PublishAsync(Actor, command, admission, Token, ReviewSamples.Result with { WorkflowId = admission.Outcome.RunId });
        return (command, admission, published);
    }
    internal PostgresReviewDecisionStore Decisions => new(Options(new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FACTORY_DECISION_TEST_CONNECTION"))
        { Database = new NpgsqlConnectionStringBuilder(Owner).Database }.ConnectionString));
    internal async Task<int> Sql(string sql, params NpgsqlParameter[] parameters)
    {
        await using var db = new NpgsqlConnection(Owner); await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db); command.Parameters.AddRange(parameters); return await command.ExecuteNonQueryAsync();
    }
    public async ValueTask DisposeAsync()
    {
        // Test-owned project only. Preserve rows and use the same command/work-item constraints.
        await Sql("""
            BEGIN;
            UPDATE lifecycle."GenerationCommands" SET "Status"='Cancelled',"HttpStatus"=499,"FailureCode"='generation_cancelled' WHERE "ProjectId"=@project AND "Status"='Running';
            UPDATE lifecycle."WorkItems" SET "ActiveRunId"=NULL,"Revision"="Revision"+1 WHERE "ProjectId"=@project AND "ActiveRunId" IS NOT NULL;
            COMMIT;
            """, new NpgsqlParameter("project", Project));
    }
}

[Collection("Lifecycle database")]
public class LifecyclePostgresTests
{
    [LifecycleTheory]
    [MemberData(nameof(LifecycleDomainTests.GoldenVectors), MemberType = typeof(LifecycleDomainTests))]
    public async Task DatabaseGenerateFingerprintMatchesGoldenWireVector(int vector, string expected)
    {
        var c = LifecycleDomainTests.GoldenCommand(vector);
        await using var db = new NpgsqlConnection(LifecycleFixture.Owner); await db.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT lifecycle.fingerprint(@p,@w,@tag,@v,@input)", db);
        query.Parameters.AddWithValue("p", c.Request.ProjectId); query.Parameters.AddWithValue("w", c.WorkItemId!.Value);
        query.Parameters.AddWithValue("tag", c.ExpectedETag!); query.Parameters.AddWithValue("v", c.WorkflowVersion);
        query.Parameters.AddWithValue("input", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(c.Request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(expected, await query.ExecuteScalarAsync());
    }

    [LifecycleFact]
    public async Task ConcurrentConflictingKeysAdmitOneCommandOnly()
    {
        await using var f = await LifecycleFixture.Create(); var command = f.Command;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<object> Admit(bool changed)
        {
            await gate.Task;
            try { return await f.Store.AdmitAsync(f.Actor, changed ? command with { Request = command.Request with { Context = "different" } } : command, f.Token); }
            catch (LifecycleException ex) { return ex.Error; }
        }
        var first = Admit(false); var second = Admit(true); gate.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results.OfType<GenerationAdmission>());
        Assert.Equal(LifecycleError.Conflict, Assert.Single(results.OfType<LifecycleError>()));
    }

    [LifecycleFact]
    public async Task CapacityIsDatabaseWideAndReplaysDoNotConsumeAnotherSlot()
    {
        await using var f = await LifecycleFixture.Create(); var commands = Enumerable.Range(0, 4).Select(_ => f.Command).ToArray();
        var admissions = new List<GenerationAdmission>();
        foreach (var command in commands) admissions.Add(await f.Store.AdmitAsync(f.Actor, command, f.Token));
        Assert.True((await f.Store.AdmitAsync(f.Actor, commands[0], f.Token)).Replayed);
        Assert.Equal(LifecycleError.Capacity, (await Assert.ThrowsAsync<LifecycleException>(() => f.Store.AdmitAsync(f.Actor, f.Command, f.Token))).Error);
        await f.Store.FinishAsync(f.Actor, f.Project, admissions[0].Outcome.RunId, f.Token, GenerationStatus.Cancelled, "generation_cancelled");
        Assert.False((await f.Store.AdmitAsync(f.Actor, f.Command, f.Token)).Replayed);
    }

    [LifecycleFact]
    public async Task ReceiptsAreScopedToPrincipalProjectAndCommandKind()
    {
        await using var first = await LifecycleFixture.Create(); await using var second = await LifecycleFixture.Create();
        var command = first.Command; var original = await first.Store.AdmitAsync(first.Actor, command, first.Token);
        await first.Store.FinishAsync(first.Actor, first.Project, original.Outcome.RunId, first.Token, GenerationStatus.Cancelled, "generation_cancelled");
        var otherProject = await second.Store.AdmitAsync(second.Actor, second.Command with { Key = command.Key }, second.Token);
        Assert.NotEqual(original.Outcome.WorkItemId, otherProject.Outcome.WorkItemId);
        await first.Sql("""
            INSERT INTO access."ProjectMemberships" ("ProjectId","PrincipalId","PrincipalKind","Role","IsActive","Revision")
            VALUES (@project,@actor,'Human','Generator',true,1)
            """, new NpgsqlParameter("project", first.Project), new NpgsqlParameter("actor", second.PrincipalId));
        var otherPrincipal = await first.Store.AdmitAsync(second.Actor, command, second.Token);
        Assert.NotEqual(original.Outcome.RunId, otherPrincipal.Outcome.RunId);
        var work = await first.Store.GetWorkItemAsync(first.Actor, first.Project, original.Outcome.WorkItemId);
        var retry = await first.Store.AdmitAsync(first.Actor, command with { WorkItemId = work.WorkItemId, ExpectedETag = work.ETag }, first.Token);
        Assert.False(retry.Replayed); Assert.NotEqual(original.Outcome.RunId, retry.Outcome.RunId);
    }

    [LifecycleFact]
    public async Task GeneratorMetadataDoesNotGrantReviewerArtifactAccess()
    {
        await using var f = await LifecycleFixture.Create(); var (_, _, published) = await f.Publish();
        await f.Sql("""UPDATE access."ProjectMemberships" SET "IsActive"=false,"Revision"="Revision"+1 WHERE "ProjectId"=@project AND "Role"='Reviewer'""", new NpgsqlParameter("project", f.Project));
        Assert.Equal(published.WorkItemId, (await f.Store.GetWorkItemAsync(f.Actor, f.Project, published.WorkItemId)).WorkItemId);
        Assert.Equal(ReviewError.Forbidden, (await Assert.ThrowsAsync<ReviewCommandException>(() => f.Decisions.GetArtifactAsync(f.Actor, f.Project, published.Publication!.CaseId, 1))).Error);
        await f.Sql("""UPDATE access."ProjectMemberships" SET "IsActive"=false,"Revision"="Revision"+1 WHERE "ProjectId"=@project AND "Role"='Generator'""", new NpgsqlParameter("project", f.Project));
        await f.Sql("""INSERT INTO access."ProjectMemberships" ("ProjectId","PrincipalId","PrincipalKind","Role","IsActive","Revision") VALUES (@project,@actor,'Human','Reader',true,1)""",
            new NpgsqlParameter("project", f.Project), new NpgsqlParameter("actor", f.PrincipalId));
        Assert.Equal(LifecycleError.Forbidden, (await Assert.ThrowsAsync<LifecycleException>(() => f.Store.GetWorkItemAsync(f.Actor, f.Project, published.WorkItemId))).Error);
    }

    [LifecycleFact]
    public async Task PublicationHasOneIdentityAndPreservesHumanDecisionAndReplay()
    {
        await using var f = await LifecycleFixture.Create(); var (command, admission, published) = await f.Publish();
        Assert.Equal(GenerationStatus.Published, published.Status);
        var work = await f.Store.GetWorkItemAsync(f.Actor, f.Project, published.WorkItemId);
        Assert.Null(work.ActiveRunId); Assert.Equal(published.RunId, work.LatestRunId); Assert.Equal(2, work.Revision);
        var artifact = await f.Decisions.GetArtifactAsync(f.Actor, f.Project, published.Publication!.CaseId, 1);
        Assert.Equal(published.RunId, ReviewArtifactSerializer.Read(artifact).WorkflowId);
        var decision = new DecisionCommand(1, 1, artifact.ContentHash, ReviewDecision.Approve, null, Guid.NewGuid());
        await f.Decisions.DecideAsync(f.Actor, f.Project, artifact.CaseId, decision);
        Assert.NotEqual(work.ETag, (await f.Store.GetWorkItemAsync(f.Actor, f.Project, work.WorkItemId)).ETag);
        var replay = await f.Store.AdmitAsync(f.Actor, command, RandomNumberGenerator.GetBytes(32));
        Assert.True(replay.Replayed); Assert.Equal(published, replay.Outcome);
        Assert.Equal(artifact.GetContent(), (await f.Decisions.GetArtifactAsync(f.Actor, f.Project, artifact.CaseId, 1)).GetContent());
        Assert.True((await f.Decisions.DecideAsync(f.Actor, f.Project, artifact.CaseId, decision)).Replayed);
    }

    [LifecycleFact]
    public async Task ConcurrentCreateHasOneWorkItemRunAndFingerprint()
    {
        await using var f = await LifecycleFixture.Create(); var command = f.Command;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<GenerationAdmission> Admit() { await start.Task; return await f.Store.AdmitAsync(f.Actor, command, RandomNumberGenerator.GetBytes(32)); }
        var first = Admit(); var second = Admit(); start.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, x => !x.Replayed); Assert.Single(results, x => x.Replayed);
        Assert.Equal(results[0].Outcome, results[1].Outcome);
        var error = await Assert.ThrowsAsync<LifecycleException>(() => f.Store.AdmitAsync(f.Actor, command with { Request = command.Request with { Task = "different" } }, f.Token));
        Assert.Equal(LifecycleError.Conflict, error.Error);
    }

    [LifecycleFact]
    public async Task DatabaseFingerprintMatchesCanonicalEncoding()
    {
        await using var f = await LifecycleFixture.Create(); var command = f.Command with { Request = f.Command.Request with { Context = "\t\r\n\u3000é😀" } };
        var admitted = await f.Store.AdmitAsync(f.Actor, command, f.Token);
        await using var db = new NpgsqlConnection(LifecycleFixture.Owner); await db.OpenAsync();
        await using var query = new NpgsqlCommand("""SELECT "Fingerprint" FROM lifecycle."GenerationCommands" WHERE "RunId"=@run""", db);
        query.Parameters.AddWithValue("run", admitted.Outcome.RunId);
        Assert.Equal(GenerationFingerprint.Compute(command), await query.ExecuteScalarAsync());
    }

    [LifecycleFact]
    public async Task WrongTokenAndChangedInputCannotPublishAndRollbackLeavesOwnership()
    {
        await using var f = await LifecycleFixture.Create(); var command = f.Command; var a = await f.Store.AdmitAsync(f.Actor, command, f.Token);
        var result = ReviewSamples.Result with { WorkflowId = a.Outcome.RunId };
        var wrong = await Assert.ThrowsAsync<LifecycleException>(() => f.Store.PublishAsync(f.Actor, command, a, RandomNumberGenerator.GetBytes(32), result));
        Assert.Equal(LifecycleError.Forbidden, wrong.Error);
        var changed = await Assert.ThrowsAsync<LifecycleException>(() => f.Store.PublishAsync(f.Actor, command with { Request = command.Request with { Context = "changed" } }, a, f.Token, result));
        Assert.Equal(LifecycleError.Integrity, changed.Error);
        var work = await f.Store.GetWorkItemAsync(f.Actor, f.Project, a.Outcome.WorkItemId);
        Assert.Equal(a.Outcome.RunId, work.ActiveRunId); Assert.Null(work.CaseId); Assert.Equal(1, work.Revision);
        await f.Store.PublishAsync(f.Actor, command, a, f.Token, result);
    }

    [LifecycleFact]
    public async Task DecisionDuringGenerationPreventsStalePublication()
    {
        await using var f = await LifecycleFixture.Create(); var (_, _, published) = await f.Publish();
        var work = await f.Store.GetWorkItemAsync(f.Actor, f.Project, published.WorkItemId);
        var command = f.Command with { WorkItemId = work.WorkItemId, ExpectedETag = work.ETag };
        var admitted = await f.Store.AdmitAsync(f.Actor, command, f.Token);
        await f.Decisions.DecideAsync(f.Actor, f.Project, work.CaseId!.Value, new(1, 1, work.ContentHash!, ReviewDecision.Reject, "needs revision", Guid.NewGuid()));
        var error = await Assert.ThrowsAsync<LifecycleException>(() => f.Store.PublishAsync(f.Actor, command, admitted, f.Token, ReviewSamples.Result with { WorkflowId = admitted.Outcome.RunId }));
        Assert.Equal(LifecycleError.StaleRevision, error.Error);
        var failed = await f.Store.FinishAsync(f.Actor, f.Project, admitted.Outcome.RunId, f.Token, GenerationStatus.PublicationFailed, "publication_stale");
        Assert.Equal(412, failed.HttpStatus);
        var current = await f.Store.GetWorkItemAsync(f.Actor, f.Project, work.WorkItemId);
        Assert.Null(current.ActiveRunId); Assert.Equal(admitted.Outcome.RunId, current.LatestRunId);
        Assert.Equal(ReviewState.Rejected, current.ReviewState); Assert.Equal(1, current.ArtifactVersion);
    }

    [LifecycleFact]
    public async Task RuntimeCannotReadSecretsMutateProtectedTablesOrAssumeOwner()
    {
        await using var db = new NpgsqlConnection(LifecycleFixture.Runtime); await db.OpenAsync();
        foreach (var sql in new[] { "SELECT * FROM lifecycle.\"GenerationCommands\"", "UPDATE review.\"ArtifactVersions\" SET \"Content\"='x'::bytea",
            "DELETE FROM review.\"DecisionEvents\"", "UPDATE access.\"ProjectMemberships\" SET \"IsActive\"=true", "CREATE TABLE public.forbidden(id int)", "SET ROLE factory_lifecycle_guard",
            "INSERT INTO review.\"ReviewCases\" (\"Id\",\"ProjectId\",\"CurrentVersion\",\"Revision\",\"State\") VALUES(gen_random_uuid(),'x',1,1,'PendingReview')" })
        {
            await using var command = new NpgsqlCommand(sql, db);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        }
    }

    [LifecycleFact]
    public async Task GeneratorRevocationAndCrossProjectReadsFailClosed()
    {
        await using var f = await LifecycleFixture.Create(PrincipalKind.Service); var command = f.Command;
        var a = await f.Store.AdmitAsync(f.Actor, command, f.Token);
        await f.Sql("""UPDATE access."ProjectMemberships" SET "IsActive"=false,"Revision"="Revision"+1 WHERE "ProjectId"=@project""", new NpgsqlParameter("project", f.Project));
        Assert.Equal(LifecycleError.Forbidden, (await Assert.ThrowsAsync<LifecycleException>(() => f.Store.PublishAsync(f.Actor, command, a, f.Token, ReviewSamples.Result with { WorkflowId = a.Outcome.RunId }))).Error);
        Assert.Equal(LifecycleError.Forbidden, (await Assert.ThrowsAsync<LifecycleException>(() => f.Store.GetWorkItemAsync(f.Actor, "other", a.Outcome.WorkItemId))).Error);
    }
}
