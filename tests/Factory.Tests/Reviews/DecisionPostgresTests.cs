using System.Data.Common;
using Factory.Core.Authorization;
using Factory.Core.Reviews;
using Factory.Infrastructure.Authorization;
using Factory.Infrastructure.Reviews;
using Factory.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Factory.Tests.Reviews;

public sealed class DecisionPostgresFactAttribute : FactAttribute
{
    public DecisionPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_DECISION_TEST_CONNECTION")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_AUTH_OWNER_CONNECTION")))
            Skip = "Requires migrated PostgreSQL, decision runtime and owner connections.";
    }
}
public sealed class DecisionPostgresTheoryAttribute : TheoryAttribute
{
    public DecisionPostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_DECISION_TEST_CONNECTION")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_AUTH_OWNER_CONNECTION")))
            Skip = "Requires migrated PostgreSQL, decision runtime and owner connections.";
    }
}

internal sealed record DecisionSeed(ReviewCaseSnapshot Case, ReviewArtifact Artifact, PrincipalRow Principal)
{
    internal TrustedIdentity Identity => new(Principal.Issuer, Principal.Subject, Principal.Kind);
    internal DecisionCommand Command => new(Case.Revision, Artifact.Version, Artifact.ContentHash, ReviewDecision.Approve, null, Guid.NewGuid());
}
internal static class DecisionTestDatabase
{
    internal static DbContextOptions<ReviewDbContext> Options(string variable = "FACTORY_DECISION_TEST_CONNECTION", params IInterceptor[] interceptors)
        => new DbContextOptionsBuilder<ReviewDbContext>().UseNpgsql(Environment.GetEnvironmentVariable(variable)!)
            .AddInterceptors(interceptors).Options;
    internal static PostgresReviewDecisionStore Store(params IInterceptor[] interceptors) => new(Options("FACTORY_DECISION_TEST_CONNECTION", interceptors));
    internal static PostgresReviewStore Publisher(params IInterceptor[] interceptors) => new(Options("FACTORY_REVIEW_TEST_CONNECTION", interceptors));
    internal static async Task<DecisionSeed> Seed(PrincipalKind kind = PrincipalKind.Human, string? issuer = null, string? subject = null)
    {
        var project = "decision-test-" + Guid.NewGuid().ToString("N");
        await using var db = new ReviewDbContext(Options("FACTORY_AUTH_OWNER_CONNECTION"));
        var existing = subject is null ? null : await db.Set<PrincipalRow>().SingleOrDefaultAsync(x => x.Issuer == (issuer ?? TestSecurity.Issuer) && x.Subject == subject);
        var principal = existing ?? new PrincipalRow { Id = Guid.NewGuid(), Issuer = issuer ?? TestSecurity.Issuer,
            Subject = subject ?? Guid.NewGuid().ToString(), Kind = kind, IsActive = true };
        db.Add(new ProjectRow { Id = project, IsActive = true }); if (existing is null) db.Add(principal);
        Assert.Equal(kind, principal.Kind);
        db.Add(new ProjectMembershipRow { ProjectId = project, PrincipalId = principal.Id, PrincipalKind = kind,
            Role = kind == PrincipalKind.Human ? ProjectRole.Reviewer : ProjectRole.Generator, IsActive = true });
        await db.SaveChangesAsync();
        var review = await Publisher().CreateAsync(ReviewSamples.Request with { ProjectId = project }, ReviewSamples.Result);
        return new(review, (await Publisher().GetArtifactAsync(project, review.Id, 1))!, principal);
    }
    internal static async Task AssertCounts(DecisionSeed seed, long revision, int decisions, int receipts)
    {
        await using var db = new ReviewDbContext(Options("FACTORY_AUTH_OWNER_CONNECTION"));
        Assert.Equal(revision, (await db.ReviewCases.SingleAsync(x => x.Id == seed.Case.Id)).Revision);
        Assert.Equal(decisions, await db.Set<DecisionEventRow>().CountAsync(x => x.CaseId == seed.Case.Id));
        Assert.Equal(receipts, await db.Set<CommandReceiptRow>().CountAsync(x => x.ProjectId == seed.Case.ProjectId));
    }
}

public class DecisionPostgresTests
{
    [DecisionPostgresTheory]
    [InlineData(ReviewDecision.Approve, ReviewState.Approved)]
    [InlineData(ReviewDecision.Reject, ReviewState.Rejected)]
    [InlineData(ReviewDecision.RequestChanges, ReviewState.ChangesRequested)]
    public async Task DecisionsPersistWithoutChangingBytesOrProvenance(ReviewDecision action, ReviewState expected)
    {
        var s = await DecisionTestDatabase.Seed();
        var result = await DecisionTestDatabase.Store().DecideAsync(s.Identity, s.Case.ProjectId, s.Case.Id,
            s.Command with { Decision = action, Reason = "private-review-reason" });
        Assert.Equal(expected, result.Snapshot.State); Assert.False(result.Replayed);
        Assert.Equal(2, result.Snapshot.Revision);
        NpgsqlConnection.ClearAllPools();
        Assert.Equal(result.Snapshot, await DecisionTestDatabase.Store().GetAsync(s.Identity, s.Case.ProjectId, s.Case.Id));
        var after = await DecisionTestDatabase.Store().GetArtifactAsync(s.Identity, s.Case.ProjectId, s.Case.Id, 1);
        Assert.Equal(s.Artifact.GetContent(), after.GetContent());
        Assert.Equal(s.Artifact.ContentHash, after.ContentHash);
        await DecisionTestDatabase.AssertCounts(s, 2, 1, 1);
        await using var auditDb = new ReviewDbContext(DecisionTestDatabase.Options("FACTORY_AUTH_OWNER_CONNECTION"));
        var audit = await auditDb.Set<DecisionEventRow>().SingleAsync(x => x.CaseId == s.Case.Id);
        var receipt = await auditDb.Set<CommandReceiptRow>().SingleAsync(x => x.DecisionId == audit.Id);
        Assert.Equal(s.Principal.Id, audit.PrincipalId); Assert.Equal(s.Principal.Issuer, audit.Issuer); Assert.Equal(s.Principal.Subject, audit.Subject);
        Assert.Equal(1, audit.ProjectRevision); Assert.Equal(1, audit.PrincipalRevision); Assert.Equal(1, audit.MembershipRevision);
        Assert.Equal(s.Artifact.ContentHash, audit.ContentHash); Assert.Equal(audit.Fingerprint, receipt.Fingerprint);
        Assert.Equal(1, receipt.CommandVersion); Assert.Equal(1, receipt.ResponseVersion);
    }

    [DecisionPostgresFact]
    public async Task LostResponseAndReplayAfterPublication_ReturnOriginalHistoricalResult()
    {
        var s = await DecisionTestDatabase.Seed(); var command = s.Command;
        var original = await DecisionTestDatabase.Store().DecideAsync(s.Identity, s.Case.ProjectId, s.Case.Id, command);
        // Treat the first result as lost. Recreate the store/connections, as after process restart.
        NpgsqlConnection.ClearAllPools();
        await DecisionTestDatabase.Publisher().PublishAsync(s.Case.Id, 2, ReviewSamples.Request with { ProjectId = s.Case.ProjectId }, ReviewSamples.Result);
        var replay = await DecisionTestDatabase.Store().DecideAsync(s.Identity, s.Case.ProjectId, s.Case.Id, command);
        Assert.True(replay.Replayed); Assert.Equal(original.Snapshot, replay.Snapshot);
        Assert.Equal(ReviewState.PendingReview, (await DecisionTestDatabase.Store().GetAsync(s.Identity, s.Case.ProjectId, s.Case.Id)).State);
        await DecisionTestDatabase.AssertCounts(s, 3, 1, 1);
        var conflict = await Assert.ThrowsAsync<ReviewCommandException>(() => DecisionTestDatabase.Store().DecideAsync(s.Identity,
            s.Case.ProjectId, s.Case.Id, command with { Reason = "changed" }));
        Assert.Equal(ReviewError.IdempotencyConflict, conflict.Error);
    }

    [DecisionPostgresTheory]
    [InlineData("revision", ReviewError.StaleRevision)]
    [InlineData("version", ReviewError.ArtifactMismatch)]
    [InlineData("hash", ReviewError.ArtifactMismatch)]
    [InlineData("service", ReviewError.Forbidden)]
    [InlineData("other-project", ReviewError.Forbidden)]
    public async Task InvalidBindingsAndAuthorizationCannotWrite(string scenario, ReviewError expected)
    {
        var s = await DecisionTestDatabase.Seed(); var command = s.Command;
        command = scenario switch { "revision" => command with { ExpectedRevision = 2 }, "version" => command with { ArtifactVersion = 2 },
            "hash" => command with { ArtifactSha256 = new string('0', 64) }, _ => command };
        var identity = scenario == "service" ? s.Identity with { Kind = PrincipalKind.Service } : s.Identity;
        var error = await Assert.ThrowsAsync<ReviewCommandException>(() => DecisionTestDatabase.Store().DecideAsync(identity,
            scenario == "other-project" ? "unknown" : s.Case.ProjectId, s.Case.Id, command));
        Assert.Equal(expected, error.Error); await DecisionTestDatabase.AssertCounts(s, 1, 0, 0);
    }

    [DecisionPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentSameKey_DeterministicallyReplaysOrConflicts(bool conflicting)
    {
        var s = await DecisionTestDatabase.Seed(); var command = s.Command;
        var barrier = new ReadBarrier(); var store = DecisionTestDatabase.Store(barrier);
        async Task<object> Run(DecisionCommand c)
        {
            try { return await store.DecideAsync(s.Identity, s.Case.ProjectId, s.Case.Id, c); }
            catch (ReviewCommandException ex) { return ex.Error; }
        }
        var results = await Task.WhenAll(Run(command), Run(conflicting ? command with { Decision = ReviewDecision.Reject, Reason = "no" } : command));
        Assert.Single(results.OfType<DecisionOutcome>(), x => !x.Replayed);
        if (conflicting) Assert.Contains(ReviewError.IdempotencyConflict, results);
        else Assert.Single(results.OfType<DecisionOutcome>(), x => x.Replayed);
        await DecisionTestDatabase.AssertCounts(s, 2, 1, 1);
    }

    [DecisionPostgresFact]
    public async Task ConcurrentDifferentKeys_OnlyOneDecisionCommits()
    {
        var s = await DecisionTestDatabase.Seed(); var store = DecisionTestDatabase.Store(new ReadBarrier());
        async Task<bool> Run()
        {
            try { await store.DecideAsync(s.Identity, s.Case.ProjectId, s.Case.Id, s.Command); return true; }
            catch (ReviewCommandException e) { Assert.Equal(ReviewError.StaleRevision, e.Error); return false; }
        }
        var results = await Task.WhenAll(Run(), Run()); Assert.Single(results, x => x);
        await DecisionTestDatabase.AssertCounts(s, 2, 1, 1);
    }

    [DecisionPostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublicationAndDecisionRace_OnlyExpectedRevisionWinnerCommits(bool decisionWins)
    {
        var s = await DecisionTestDatabase.Seed(); var gate = new UpdateGate();
        Task loser = decisionWins
            ? DecisionTestDatabase.Publisher(gate).PublishAsync(s.Case.Id, 1, ReviewSamples.Request with { ProjectId = s.Case.ProjectId }, ReviewSamples.Result)
            : DecisionTestDatabase.Store(gate).DecideAsync(s.Identity, s.Case.ProjectId, s.Case.Id, s.Command);
        await await Task.WhenAny(gate.Entered.Task, loser).WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (decisionWins) await DecisionTestDatabase.Store().DecideAsync(s.Identity, s.Case.ProjectId, s.Case.Id, s.Command);
            else await DecisionTestDatabase.Publisher().PublishAsync(s.Case.Id, 1, ReviewSamples.Request with { ProjectId = s.Case.ProjectId }, ReviewSamples.Result);
        }
        finally { gate.Release.TrySetResult(); }
        if (decisionWins) await Assert.ThrowsAsync<ReviewConflictException>(() => loser);
        else Assert.Equal(ReviewError.StaleRevision, (await Assert.ThrowsAsync<ReviewCommandException>(() => loser)).Error);
        await DecisionTestDatabase.AssertCounts(s, 2, decisionWins ? 1 : 0, decisionWins ? 1 : 0);
    }

    [DecisionPostgresTheory]
    [InlineData("DecisionEvents")]
    [InlineData("CommandReceipts")]
    public async Task FailureAfterClaim_RollsBackAllWrites(string table)
    {
        var s = await DecisionTestDatabase.Seed();
        await Assert.ThrowsAsync<DbUpdateException>(() => DecisionTestDatabase.Store(new FailInsert(table)).DecideAsync(s.Identity, s.Case.ProjectId, s.Case.Id, s.Command));
        await DecisionTestDatabase.AssertCounts(s, 1, 0, 0);
    }

    [DecisionPostgresTheory]
    [InlineData("no-event")]
    [InlineData("no-receipt")]
    [InlineData("no-transition")]
    [InlineData("wrong-state")]
    [InlineData("wrong-revision")]
    public async Task DirectSqlBypass_FailsDeferredChecksAtCommit(string scenario)
    {
        var s = await DecisionTestDatabase.Seed();
        await using var db = new ReviewDbContext(DecisionTestDatabase.Options());
        await using var tx = await db.Database.BeginTransactionAsync();
        if (scenario != "no-transition") await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE review."ReviewCases" SET "State" = 'Approved', "Revision" = 2 WHERE "Id" = {s.Case.Id}
            """);
        if (scenario != "no-event")
        {
            var id = Guid.NewGuid(); var key = Guid.NewGuid();
            var action = scenario == "wrong-state" ? "Reject" : "Approve";
            var state = scenario == "wrong-state" ? "Rejected" : "Approved";
            long expected = scenario == "wrong-revision" ? 2 : 1;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO review."DecisionEvents" ("Id","ProjectId","CaseId","Version","ContentHash","PrincipalId","Issuer","Subject",
                    "ExpectedRevision","ResultRevision","Decision","Reason","ResultState","CommandVersion","Key")
                VALUES ({id},{s.Case.ProjectId},{s.Case.Id},1,{s.Artifact.ContentHash},{s.Principal.Id},{s.Principal.Issuer},{s.Principal.Subject},
                    {expected},{expected + 1},{action},'reason',{state},1,{key})
                """);
            if (scenario != "no-receipt") await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO review."CommandReceipts" ("ProjectId","PrincipalId","CommandVersion","Key","DecisionId","Fingerprint","ResponseVersion")
                SELECT "ProjectId","PrincipalId",1,"Key","Id","Fingerprint",1 FROM review."DecisionEvents" WHERE "Id" = {id}
                """);
        }
        // Every statement above succeeded. The missing/mismatched relationship must fail at COMMIT.
        var error = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        Assert.Equal("23514", error.SqlState);
        await DecisionTestDatabase.AssertCounts(s, 1, 0, 0);
    }

    [DecisionPostgresTheory]
    [InlineData("membership")]
    [InlineData("principal")]
    [InlineData("project")]
    public async Task RevocationCommitsBeforeAuthorization_DecisionWaitsThenDenies(string target)
    {
        var s = await DecisionTestDatabase.Seed();
        await using var owner = new ReviewDbContext(DecisionTestDatabase.Options("FACTORY_AUTH_OWNER_CONNECTION"));
        await using var tx = await owner.Database.BeginTransactionAsync();
        await Deactivate(owner, s, target);
        var probe = new AuthorizationProbe();
        var pending = DecisionTestDatabase.Store(probe).DecideAsync(s.Identity, s.Case.ProjectId, s.Case.Id, s.Command);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForBlocked(owner, probe.ProcessId);
        await tx.CommitAsync();
        Assert.Equal(ReviewError.Forbidden, (await Assert.ThrowsAsync<ReviewCommandException>(() => pending)).Error);
        await DecisionTestDatabase.AssertCounts(s, 1, 0, 0);
    }

    [DecisionPostgresTheory]
    [InlineData("membership")]
    [InlineData("principal")]
    [InlineData("project")]
    public async Task DecisionHoldsAuthorizationLocks_RevocationWaitsForCommit(string target)
    {
        var s = await DecisionTestDatabase.Seed(); var gate = new UpdateGate();
        var decision = DecisionTestDatabase.Store(gate).DecideAsync(s.Identity, s.Case.ProjectId, s.Case.Id, s.Command);
        await await Task.WhenAny(gate.Entered.Task, decision).WaitAsync(TimeSpan.FromSeconds(10));
        await using var owner = new ReviewDbContext(DecisionTestDatabase.Options("FACTORY_AUTH_OWNER_CONNECTION"));
        await owner.Database.OpenConnectionAsync();
        var revokerPid = ((NpgsqlConnection)owner.Database.GetDbConnection()).ProcessID;
        var revoke = Deactivate(owner, s, target);
        try
        {
            await using var monitor = new ReviewDbContext(DecisionTestDatabase.Options("FACTORY_AUTH_OWNER_CONNECTION"));
            await WaitForBlocked(monitor, revokerPid);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.False((await decision).Replayed); await revoke;
        await DecisionTestDatabase.AssertCounts(s, 2, 1, 1);
        Assert.Equal(ReviewError.Forbidden, (await Assert.ThrowsAsync<ReviewCommandException>(() =>
            DecisionTestDatabase.Store().GetAsync(s.Identity, s.Case.ProjectId, s.Case.Id))).Error);
    }

    private static Task<int> Deactivate(ReviewDbContext db, DecisionSeed s, string target) => target switch
    {
        "project" => db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE access."Projects" SET "IsActive" = false, "Revision" = "Revision" + 1 WHERE "Id" = {s.Case.ProjectId}"""),
        "principal" => db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE access."Principals" SET "IsActive" = false, "Revision" = "Revision" + 1 WHERE "Id" = {s.Principal.Id}"""),
        _ => db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE access."ProjectMemberships" SET "IsActive" = false, "Revision" = "Revision" + 1 WHERE "PrincipalId" = {s.Principal.Id}""")
    };
    private static async Task WaitForBlocked(ReviewDbContext db, int processId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync();
        while (DateTime.UtcNow < deadline)
        {
            await using var query = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity WHERE pid = @process AND wait_event_type = 'Lock')", connection) { CommandTimeout = 3 };
            query.Parameters.AddWithValue("process", processId);
            if ((bool)(await query.ExecuteScalarAsync())!) return;
            await Task.Delay(20);
        }
        Assert.Fail("Expected a PostgreSQL lock wait was not observed.");
    }

    [DecisionPostgresFact]
    public async Task RuntimeAndReaderCannotMutateProtectedObjectsOrEscalate()
    {
        var s = await DecisionTestDatabase.Seed();
        foreach (var variable in new[] { "FACTORY_DECISION_TEST_CONNECTION", "FACTORY_AUTH_READER_CONNECTION" })
        {
            await using var db = new ReviewDbContext(DecisionTestDatabase.Options(variable));
            foreach (var sql in new[] {
                "UPDATE review.\"ArtifactVersions\" SET \"ContentHash\" = 'bad'",
                "DELETE FROM review.\"ArtifactVersions\"", "TRUNCATE review.\"DecisionEvents\"",
                "UPDATE review.\"DecisionEvents\" SET \"Reason\" = 'changed'", "DELETE FROM review.\"CommandReceipts\"",
                "UPDATE access.\"ProjectMemberships\" SET \"IsActive\" = true",
                "UPDATE access.\"Principals\" SET \"Kind\" = 'Human'", "CREATE TABLE review.forged (id int)",
                "ALTER TABLE review.\"DecisionEvents\" DISABLE TRIGGER ALL", "SET ROLE factory_review_guard",
                "CREATE TABLE public.forged (id int)", "CREATE TEMP TABLE forged (id int)",
                "INSERT INTO access.\"Projects\" (\"Id\",\"IsActive\",\"Revision\") VALUES ('forged',true,1)" })
            {
                var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
                Assert.Equal("42501", ex.SqlState);
            }
        }
        await using var owner = new ReviewDbContext(DecisionTestDatabase.Options("FACTORY_AUTH_OWNER_CONNECTION"));
        foreach (var sql in new[] { "DELETE FROM review.\"DecisionEvents\" WHERE false", "DELETE FROM review.\"CommandReceipts\" WHERE false" })
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => owner.Database.ExecuteSqlRawAsync(sql))).SqlState);
        await using var reader = new ReviewDbContext(DecisionTestDatabase.Options("FACTORY_AUTH_READER_CONNECTION"));
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => reader.Database.SqlQuery<Guid>($"""
            SELECT "PrincipalId" AS "Value" FROM review.lock_reviewer({s.Case.ProjectId},{s.Principal.Issuer},{s.Principal.Subject})
            """).SingleAsync())).SqlState);
    }

    [DecisionPostgresFact]
    public async Task HelperCatalog_HasSafeOwnerSearchPathAndNoPublicExecute()
    {
        await using var db = new ReviewDbContext(DecisionTestDatabase.Options("FACTORY_AUTH_OWNER_CONNECTION"));
        Assert.True(await db.Database.SqlQuery<bool>($"""
            SELECT p.prosecdef AND r.rolname = 'factory_review_guard' AND NOT r.rolcanlogin AND NOT r.rolsuper
                AND NOT r.rolinherit AND NOT r.rolcreaterole AND NOT r.rolcreatedb AND NOT r.rolbypassrls
                AND p.proconfig @> ARRAY['search_path=pg_catalog, pg_temp']
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.aclexplode(p.proacl) a WHERE a.grantee = 0 AND a.privilege_type = 'EXECUTE')
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.roleid = r.oid OR m.member = r.oid)
                AND NOT pg_catalog.has_schema_privilege(r.oid,'review','CREATE') AS "Value"
            FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_roles r ON r.oid = p.proowner
            WHERE p.oid = 'review.lock_reviewer(text,text,text)'::regprocedure
            """).SingleAsync());
    }

    [DecisionPostgresFact]
    public async Task IdempotencyScope_SeparatesProjectsAndPrincipalsButNotCases()
    {
        var a = await DecisionTestDatabase.Seed(); var b = await DecisionTestDatabase.Seed(); var key = Guid.NewGuid();
        var store = DecisionTestDatabase.Store();
        await store.DecideAsync(a.Identity, a.Case.ProjectId, a.Case.Id, a.Command with { IdempotencyKey = key });
        await store.DecideAsync(b.Identity, b.Case.ProjectId, b.Case.Id, b.Command with { IdempotencyKey = key });
        await using var owner = new ReviewDbContext(DecisionTestDatabase.Options("FACTORY_AUTH_OWNER_CONNECTION"));
        owner.Add(new ProjectMembershipRow { ProjectId = a.Case.ProjectId, PrincipalId = b.Principal.Id,
            PrincipalKind = PrincipalKind.Human, Role = ProjectRole.Reviewer, IsActive = true });
        await owner.SaveChangesAsync();
        var second = await DecisionTestDatabase.Publisher().CreateAsync(ReviewSamples.Request with { ProjectId = a.Case.ProjectId }, ReviewSamples.Result);
        var artifact = (await DecisionTestDatabase.Publisher().GetArtifactAsync(a.Case.ProjectId, second.Id, 1))!;
        var command = a.Command with { IdempotencyKey = key, ArtifactSha256 = artifact.ContentHash };
        Assert.Equal(ReviewError.IdempotencyConflict, (await Assert.ThrowsAsync<ReviewCommandException>(() =>
            store.DecideAsync(a.Identity, a.Case.ProjectId, second.Id, command))).Error);
        var result = await store.DecideAsync(b.Identity, a.Case.ProjectId, second.Id, command);
        Assert.False(result.Replayed); Assert.Equal(b.Principal.Id, result.Snapshot.Decision!.PrincipalId);
        await Deactivate(owner, a, "membership");
        Assert.Equal(ReviewError.Forbidden, (await Assert.ThrowsAsync<ReviewCommandException>(() =>
            store.DecideAsync(a.Identity, a.Case.ProjectId, a.Case.Id, a.Command with { IdempotencyKey = key }))).Error);
    }

    [DecisionPostgresFact]
    public async Task AuthorizedProjectCannotAddressAnotherProjectsCase()
    {
        var a = await DecisionTestDatabase.Seed(); var b = await DecisionTestDatabase.Seed(); var store = DecisionTestDatabase.Store();
        Assert.Equal(ReviewError.NotFound, (await Assert.ThrowsAsync<ReviewCommandException>(() => store.GetAsync(a.Identity, a.Case.ProjectId, b.Case.Id))).Error);
        Assert.Equal(ReviewError.NotFound, (await Assert.ThrowsAsync<ReviewCommandException>(() => store.GetArtifactAsync(a.Identity, a.Case.ProjectId, b.Case.Id, 1))).Error);
        Assert.Equal(ReviewError.NotFound, (await Assert.ThrowsAsync<ReviewCommandException>(() => store.DecideAsync(a.Identity, a.Case.ProjectId, b.Case.Id, b.Command))).Error);
        await DecisionTestDatabase.AssertCounts(b, 1, 0, 0);
    }

    [DecisionPostgresTheory]
    [InlineData("identity", "42501")]
    [InlineData("hash", "23503")]
    [InlineData("cross-project", "23503")]
    [InlineData("receipt", "23503")]
    public async Task RawSqlCannotForgeIdentityArtifactOrReceiptBinding(string scenario, string code)
    {
        var s = await DecisionTestDatabase.Seed(); var other = await DecisionTestDatabase.Seed();
        await using var db = new ReviewDbContext(DecisionTestDatabase.Options());
        await using var tx = await db.Database.BeginTransactionAsync();
        var id = Guid.NewGuid(); var key = Guid.NewGuid();
        var actor = scenario == "identity" ? other.Principal.Id : s.Principal.Id;
        var caseId = scenario == "cross-project" ? other.Case.Id : s.Case.Id;
        var hash = scenario == "hash" ? new string('0',64) : s.Artifact.ContentHash;
        async Task Insert() => await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO review."DecisionEvents" ("Id","ProjectId","CaseId","Version","ContentHash","PrincipalId","Issuer","Subject",
                "ExpectedRevision","ResultRevision","Decision","Reason","ResultState","CommandVersion","Key")
            VALUES ({id},{s.Case.ProjectId},{caseId},1,{hash},{actor},{s.Principal.Issuer},{s.Principal.Subject},1,2,'Approve',NULL,'Approved',1,{key})
            """);
        if (scenario == "receipt")
        {
            await Insert();
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO review."CommandReceipts" ("ProjectId","PrincipalId","CommandVersion","Key","DecisionId","Fingerprint","ResponseVersion")
                SELECT "ProjectId","PrincipalId",1,{Guid.NewGuid()},"Id","Fingerprint",1 FROM review."DecisionEvents" WHERE "Id"={id}
                """));
            Assert.Equal(code, error.SqlState);
        }
        else Assert.Equal(code, (await Assert.ThrowsAsync<PostgresException>(Insert)).SqlState);
        await tx.RollbackAsync(); await DecisionTestDatabase.AssertCounts(s, 1, 0, 0);
    }

    [DecisionPostgresFact]
    public async Task PublicationWriterCannotApproveByDirectSqlEvenWithRevisionIncrement()
    {
        var s = await DecisionTestDatabase.Seed();
        await using var db = new ReviewDbContext(DecisionTestDatabase.Options("FACTORY_REVIEW_TEST_CONNECTION"));
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE review."ReviewCases" SET "State"='Approved', "Revision"=2 WHERE "Id"={s.Case.Id}""");
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync())).SqlState);
        await DecisionTestDatabase.AssertCounts(s, 1, 0, 0);
    }

    [DecisionPostgresFact]
    public async Task SnapshotRemainsConsistentWhilePublicationIsUncommitted()
    {
        var s = await DecisionTestDatabase.Seed(); var store = DecisionTestDatabase.Store();
        var first = await store.DecideAsync(s.Identity, s.Case.ProjectId, s.Case.Id, s.Command);
        var gate = new PublicationCommitGate();
        var publish = DecisionTestDatabase.Publisher(gate).PublishAsync(s.Case.Id, 2, ReviewSamples.Request with { ProjectId = s.Case.ProjectId }, ReviewSamples.Result);
        await await Task.WhenAny(gate.Entered.Task, publish).WaitAsync(TimeSpan.FromSeconds(10));
        try { Assert.Equal(first.Snapshot, await store.GetAsync(s.Identity, s.Case.ProjectId, s.Case.Id)); }
        finally { gate.Release.TrySetResult(); }
        await publish;
        var current = await store.GetAsync(s.Identity, s.Case.ProjectId, s.Case.Id);
        Assert.Equal(3, current.Revision); Assert.Equal(2, current.CurrentVersion); Assert.Equal(ReviewState.PendingReview, current.State);
        Assert.Null(current.Decision);
        Assert.Equal((await store.GetArtifactAsync(s.Identity, s.Case.ProjectId, s.Case.Id, 2)).ContentHash, current.ContentHash);
    }
    private sealed class PublicationCommitGate : DbTransactionInterceptor
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); return result; }
    }

    [DecisionPostgresTheory]
    [InlineData("service")] [InlineData("generator")] [InlineData("inactive")]
    public async Task RawSqlAuthorizationHelperRejectsNonReviewers(string scenario)
    {
        var s = await DecisionTestDatabase.Seed(scenario == "service" ? PrincipalKind.Service : PrincipalKind.Human);
        await using var owner = new ReviewDbContext(DecisionTestDatabase.Options("FACTORY_AUTH_OWNER_CONNECTION"));
        if (scenario == "generator") await owner.Database.ExecuteSqlInterpolatedAsync($"""UPDATE access."ProjectMemberships" SET "Role"='Generator' WHERE "PrincipalId"={s.Principal.Id}""");
        if (scenario == "inactive") await Deactivate(owner, s, "membership");
        await using var db = new ReviewDbContext(DecisionTestDatabase.Options());
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.SqlQuery<Guid>($"""
            SELECT "PrincipalId" AS "Value" FROM review.lock_reviewer({s.Case.ProjectId},{s.Principal.Issuer},{s.Principal.Subject})
            """).SingleAsync())).SqlState);
        await DecisionTestDatabase.AssertCounts(s, 1, 0, 0);
    }

    private sealed class FailInsert(string table) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO review.\"" + table + "\"")) throw new InvalidOperationException("synthetic_private_failure");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
    internal sealed class UpdateGate : DbCommandInterceptor
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE review.\"ReviewCases\""))
            { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
            return result;
        }
    }
    private sealed class AuthorizationProbe : DbCommandInterceptor
    {
        internal int ProcessId { get; private set; }
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("review.lock_reviewer"))
            { ProcessId = ((NpgsqlConnection)command.Connection!).ProcessID; Entered.TrySetResult(); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ReadBarrier : DbCommandInterceptor
    {
        private int reads;
        private readonly TaskCompletionSource both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM review.\"ReviewCases\""))
            { if (Interlocked.Increment(ref reads) == 2) both.TrySetResult(); await both.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
            return result;
        }
    }
}
