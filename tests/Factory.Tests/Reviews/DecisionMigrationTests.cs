using Factory.Core.Authorization;
using Factory.Core.Reviews;
using Factory.Infrastructure.Authorization;
using Factory.Infrastructure.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Factory.Tests.Reviews;

public class DecisionMigrationTests
{
    // Historical 5B.2 migration matrix. M6's closed-cohort upgrade has its own tests;
    // these fixtures intentionally exercise the schema on which legacy creation existed.
    private const string DecisionBaseline = "20261014010250_AlignDecisionReasonPolicy";
    [DecisionPostgresTheory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task FreshAndUpgradeMigrations_EnforceTransactionsAndPreserveArtifacts(int stage)
    {
        // Dedicated retained databases. No DROP, downgrade, or mutation of the operator's source database.
        var database = "factory_5b2_" + (stage > 0 ? "upgrade_" : "fresh_") + Guid.NewGuid().ToString("N");
        var owner = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FACTORY_AUTH_OWNER_CONNECTION"));
        await using (var connection = new NpgsqlConnection(owner.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand("CREATE DATABASE " + database, connection);
            await create.ExecuteNonQueryAsync();
        }
        owner.Database = database;
        var options = new DbContextOptionsBuilder<ReviewDbContext>().UseNpgsql(owner.ConnectionString).Options;
        await using var db = new ReviewDbContext(options);
        var migrator = db.GetService<IMigrator>();
        if (stage > 0) await migrator.MigrateAsync(stage == 1 ? "20261013084222_ProjectAuthorization" : "20261013211346_SecureHumanApprovalTransactions");
        else await migrator.MigrateAsync(DecisionBaseline);
        var publisher = new PostgresReviewStore(options);
        var review = await publisher.CreateAsync(ReviewSamples.Request, ReviewSamples.Result);
        var before = (await publisher.GetArtifactAsync(review.ProjectId, review.Id, 1))!;
        var principal = new PrincipalRow { Id = Guid.NewGuid(), Issuer = "https://migration.test", Subject = "reviewer", Kind = PrincipalKind.Human, IsActive = true };
        db.Add(principal); db.Add(new ProjectRow { Id = review.ProjectId, IsActive = true });
        db.Add(new ProjectMembershipRow { ProjectId = review.ProjectId, PrincipalId = principal.Id, PrincipalKind = PrincipalKind.Human, Role = ProjectRole.Reviewer, IsActive = true });
        await db.SaveChangesAsync();
        if (stage == 3)
        {
            // The old applied constraint admitted tabs. The additive migration must fail closed,
            // not rewrite an immutable decision or leave its old constraint silently removed.
            await using (var historicalTransaction = await db.Database.BeginTransactionAsync())
            {
                await DecisionReasonTests.Insert(db,new(review,before,principal),ReviewDecision.Reject,"\t");
                await historicalTransaction.CommitAsync();
            }
            Assert.Equal("23514",(await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(DecisionBaseline))).SqlState);
            Assert.Equal(3,(await db.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal("\t",await db.Database.SqlQueryRaw<string>("""SELECT "Reason" AS "Value" FROM review."DecisionEvents" """).SingleAsync());
            Assert.Equal(1,await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM pg_catalog.pg_constraint WHERE conname='decision_reason'""").SingleAsync());
            Assert.Equal(before.GetContent(),(await publisher.GetArtifactAsync(review.ProjectId,review.Id,1))!.GetContent());
            return;
        }
        DecisionOutcome? historical = null;
        if (stage == 2)
        {
            var oldCase = await publisher.CreateAsync(ReviewSamples.Request,ReviewSamples.Result);
            var oldArtifact = (await publisher.GetArtifactAsync(oldCase.ProjectId,oldCase.Id,1))!;
            historical = await new PostgresReviewDecisionStore(options).DecideAsync(
                new(principal.Issuer,principal.Subject,PrincipalKind.Human),oldCase.ProjectId,oldCase.Id,
                new(1,1,oldArtifact.ContentHash,ReviewDecision.Reject,"Preserved historical reason",Guid.NewGuid()));
        }
        if (stage > 0) await migrator.MigrateAsync(DecisionBaseline);
        Assert.Equal(4, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.False(db.Database.HasPendingModelChanges());
        if (historical is not null)
            Assert.Equal(historical.Snapshot, await new PostgresReviewDecisionStore(options).GetAsync(
                new(principal.Issuer,principal.Subject,PrincipalKind.Human),review.ProjectId,historical.Snapshot.Id));
        Assert.Equal(review, await publisher.GetCaseAsync(review.ProjectId, review.Id));
        var after = (await publisher.GetArtifactAsync(review.ProjectId, review.Id, 1))!;
        Assert.Equal(before.GetContent(), after.GetContent()); Assert.Equal(before.ContentHash, after.ContentHash);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "scripts", "decision-runtime-grants.sql"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var name in new[] { "review-runtime-grants.sql", "authorization-runtime-grants.sql", "decision-runtime-grants.sql" })
            await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(root.FullName, "scripts", name)));
        var runtime = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FACTORY_DECISION_TEST_CONNECTION")) { Database = database };
        var runtimeOptions = new DbContextOptionsBuilder<ReviewDbContext>().UseNpgsql(runtime.ConnectionString).Options;
        var store = new PostgresReviewDecisionStore(runtimeOptions);
        var identity = new TrustedIdentity(principal.Issuer, principal.Subject, PrincipalKind.Human);
        var result = await store.DecideAsync(identity, review.ProjectId, review.Id, new(1, 1, before.ContentHash, ReviewDecision.Approve, null, Guid.NewGuid()));
        Assert.Equal(ReviewState.Approved, result.Snapshot.State);
        Assert.Equal(before.GetContent(), (await store.GetArtifactAsync(identity, review.ProjectId, review.Id, 1)).GetContent());
        var second = await publisher.CreateAsync(ReviewSamples.Request, ReviewSamples.Result);
        await using var runtimeDb = new ReviewDbContext(runtimeOptions);
        await using var tx = await runtimeDb.Database.BeginTransactionAsync();
        await runtimeDb.Database.ExecuteSqlInterpolatedAsync($"""UPDATE review."ReviewCases" SET "State"='Approved', "Revision"=2 WHERE "Id"={second.Id}""");
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync())).SqlState);
        Assert.Equal(ReviewState.PendingReview, (await publisher.GetCaseAsync(review.ProjectId, second.Id))!.State);
    }
}
