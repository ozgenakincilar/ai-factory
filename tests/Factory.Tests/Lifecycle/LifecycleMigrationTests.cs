using Factory.Core.Authorization;
using Factory.Core.Reviews;
using Factory.Infrastructure.Authorization;
using Factory.Infrastructure.Reviews;
using Factory.Tests.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Factory.Tests.Lifecycle;

[Collection("Lifecycle database")]
public class LifecycleMigrationTests
{
    [LifecycleFact]
    public async Task UpgradePreservesHistoricalRowsAndClosesLegacyCreationAtomically()
    {
        var owner = new NpgsqlConnectionStringBuilder(LifecycleFixture.Owner);
        var name = "factory_m6_upgrade_" + Guid.NewGuid().ToString("N");
        await using (var source = new NpgsqlConnection(owner.ConnectionString))
        {
            await source.OpenAsync();
            await using var create = new NpgsqlCommand("CREATE DATABASE " + name, source); await create.ExecuteNonQueryAsync();
        }
        owner.Database = name;
        await using var db = new ReviewDbContext(LifecycleFixture.Options(owner.ConnectionString));
        var migrator = db.GetService<IMigrator>(); await migrator.MigrateAsync("20261014010250_AlignDecisionReasonPolicy");
        var publisher = new PostgresReviewStore(LifecycleFixture.Options(owner.ConnectionString));
        var review = await publisher.CreateAsync(ReviewSamples.Request, ReviewSamples.Result);
        var undecided = await publisher.CreateAsync(ReviewSamples.Request, ReviewSamples.Result);
        var compatibilityCases = new List<ReviewCaseSnapshot>();
        for (var i = 0; i < 3; i++) compatibilityCases.Add(await publisher.CreateAsync(ReviewSamples.Request, ReviewSamples.Result));
        var principal = new PrincipalRow { Id=Guid.NewGuid(),Issuer="https://migration.test",Subject="reviewer",Kind=PrincipalKind.Human,IsActive=true };
        db.Add(new ProjectRow { Id=review.ProjectId,IsActive=true }); db.Add(principal);
        db.Add(new ProjectMembershipRow { ProjectId=review.ProjectId,PrincipalId=principal.Id,PrincipalKind=PrincipalKind.Human,Role=ProjectRole.Reviewer,IsActive=true });
        await db.SaveChangesAsync();
        var decisions = new PostgresReviewDecisionStore(LifecycleFixture.Options(owner.ConnectionString));
        var actor = new TrustedIdentity(principal.Issuer,principal.Subject,principal.Kind);
        var artifact = (await publisher.GetArtifactAsync(review.ProjectId,review.Id,1))!;
        var command = new DecisionCommand(1,1,artifact.ContentHash,ReviewDecision.Reject,"historical explanation",Guid.NewGuid());
        var outcome = await decisions.DecideAsync(actor,review.ProjectId,review.Id,command);
        var before = await Audit();
        await migrator.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        // M6 plus the additive M7.1, M7.2, M7.3 and M8.1 migrations.
        Assert.Equal(9,(await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(before,await Audit());
        Assert.Equal(artifact.GetContent(),(await publisher.GetArtifactAsync(review.ProjectId,review.Id,1))!.GetContent());
        Assert.Equal("Legacy",await db.Database.SqlQueryRaw<string>("""SELECT "ManagementMode" AS "Value" FROM review."ReviewCases" LIMIT 1""").SingleAsync());
        Assert.Equal(0,await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM review."ReviewCaseLineage" """).SingleAsync());
        Assert.Equal(outcome.Snapshot,(await decisions.DecideAsync(actor,review.ProjectId,review.Id,command)).Snapshot);
        await Assert.ThrowsAsync<DbUpdateException>(()=>publisher.CreateAsync(ReviewSamples.Request,ReviewSamples.Result));
        // Restricted legacy writer can still publish an existing historical case after explicit grants.
        foreach(var file in new[]{"review-runtime-grants.sql","authorization-runtime-grants.sql","decision-runtime-grants.sql","lifecycle-runtime-grants.sql","orchestration-runtime-grants.sql"})
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(Root(),"scripts",file)));
        var writer = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FACTORY_REVIEW_TEST_CONNECTION")) { Database=name };
        var restricted = new PostgresReviewStore(LifecycleFixture.Options(writer.ConnectionString));
        await restricted.PublishAsync(undecided.Id,1,ReviewSamples.Request,ReviewSamples.Result);
        await Assert.ThrowsAsync<DbUpdateException>(()=>restricted.CreateAsync(ReviewSamples.Request,ReviewSamples.Result));
        // Existing decisions still work through the restricted decision runtime after upgrade.
        var runtime = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FACTORY_DECISION_TEST_CONNECTION")){Database=name};
        var next = (await restricted.GetArtifactAsync(undecided.ProjectId,undecided.Id,2))!;
        var decided = await new PostgresReviewDecisionStore(LifecycleFixture.Options(runtime.ConnectionString)).DecideAsync(actor,undecided.ProjectId,undecided.Id,
            new(2,2,next.ContentHash,ReviewDecision.Approve,null,Guid.NewGuid()));
        Assert.Equal(ReviewState.Approved,decided.Snapshot.State);
        var upgradedDecisions = new PostgresReviewDecisionStore(LifecycleFixture.Options(runtime.ConnectionString));
        foreach (var pair in compatibilityCases.Zip(new[] { ReviewDecision.Approve, ReviewDecision.Reject, ReviewDecision.RequestChanges }))
        {
            var oldBytes = (await restricted.GetArtifactAsync(review.ProjectId, pair.First.Id, 1))!;
            var decisionCommand = new DecisionCommand(1, 1, oldBytes.ContentHash, pair.Second, "upgrade compatibility", Guid.NewGuid());
            var decisionResult = await upgradedDecisions.DecideAsync(actor, review.ProjectId, pair.First.Id, decisionCommand);
            Assert.Equal(decisionResult.Snapshot, await upgradedDecisions.GetAsync(actor, review.ProjectId, pair.First.Id));
            Assert.Equal(oldBytes.GetContent(), (await upgradedDecisions.GetArtifactAsync(actor, review.ProjectId, pair.First.Id, 1)).GetContent());
            await restricted.PublishAsync(pair.First.Id, 2, ReviewSamples.Request, ReviewSamples.Result);
            var historicalReplay = await upgradedDecisions.DecideAsync(actor, review.ProjectId, pair.First.Id, decisionCommand);
            Assert.True(historicalReplay.Replayed); Assert.Equal(decisionResult.Snapshot, historicalReplay.Snapshot);
            Assert.Equal(ReviewState.PendingReview, (await upgradedDecisions.GetAsync(actor, review.ProjectId, pair.First.Id)).State);
        }
        async Task<string> Audit()=>await db.Database.SqlQueryRaw<string>("""
            SELECT (SELECT jsonb_agg(to_jsonb(d) ORDER BY "Id") FROM review."DecisionEvents" d)::text ||
                (SELECT jsonb_agg(to_jsonb(r) ORDER BY "Key") FROM review."CommandReceipts" r)::text AS "Value"
            """).SingleAsync();
    }

    internal static string Root()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"AGENTS.md"))) root=root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("repository_not_found");
    }
}
