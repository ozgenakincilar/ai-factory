using System.Data.Common;
using Factory.Core.Reviews;
using Factory.Infrastructure.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Factory.Tests.Reviews;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_REVIEW_TEST_CONNECTION")))
            Skip = "Real PostgreSQL required: set FACTORY_REVIEW_TEST_CONNECTION to the restricted writer connection.";
    }
}

public class PostgresReviewTests
{
    private static DbContextOptions<ReviewDbContext> Options => new DbContextOptionsBuilder<ReviewDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("FACTORY_REVIEW_TEST_CONNECTION")!).Options;

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task VersionsPersistAcrossContextsAndConnections_WithAuditAndProjectIsolation()
    {
        var store = new PostgresReviewStore(Options);
        var review = await store.CreateAsync(ReviewSamples.Request, ReviewSamples.Result);
        var first = await store.GetArtifactAsync(review.ProjectId, review.Id, 1);
        var second = await store.PublishAsync(review.Id, review.Revision, ReviewSamples.Request, ReviewSamples.Result);
        NpgsqlConnection.ClearAllPools();
        var reopened = new PostgresReviewStore(Options);
        Assert.Equal(second, await reopened.GetCaseAsync(review.ProjectId, review.Id));
        Assert.Equal(first!.GetContent(), (await reopened.GetArtifactAsync(review.ProjectId, review.Id, 1))!.GetContent());
        Assert.Equal(2, second.CurrentVersion);
        Assert.Equal(ReviewState.PendingReview, second.State);
        Assert.Null(await reopened.GetArtifactAsync("another-project", review.Id, 1));
        await using var db = new ReviewDbContext(Options);
        Assert.Equal(2, await db.PublicationEvents.CountAsync(x => x.CaseId == review.Id));
        Assert.Equal(2, await db.ArtifactVersions.CountAsync(x => x.CaseId == review.Id));
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ConcurrentPublishers_OnlyOneWins_WithoutOrphanArtifacts()
    {
        var store = new PostgresReviewStore(Options);
        var review = await store.CreateAsync(ReviewSamples.Request, ReviewSamples.Result);
        var concurrent = new PostgresReviewStore(new DbContextOptionsBuilder<ReviewDbContext>(Options)
            .AddInterceptors(new SynchronizeReviewReads()).Options);
        async Task<bool> Publish()
        {
            try { await concurrent.PublishAsync(review.Id, 1, ReviewSamples.Request, ReviewSamples.Result); return true; }
            catch (ReviewConflictException) { return false; }
        }
        var outcomes = await Task.WhenAll(Publish(), Publish());
        Assert.Single(outcomes, x => x);
        Assert.Single(outcomes, x => !x);
        await using var db = new ReviewDbContext(Options);
        Assert.Equal(2, await db.ArtifactVersions.CountAsync(x => x.CaseId == review.Id));
        Assert.Equal(2, await db.PublicationEvents.CountAsync(x => x.CaseId == review.Id));
    }

    private sealed class SynchronizeReviewReads : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM review.\"ReviewCases\""))
            {
                if (Interlocked.Increment(ref _readCount) == 2) _bothRead.TrySetResult();
                await _bothRead.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task FailureAfterRevisionClaim_RollsBackWholeTransaction()
    {
        var store = new PostgresReviewStore(Options);
        var review = await store.CreateAsync(ReviewSamples.Request, ReviewSamples.Result);
        var builder = new DbContextOptionsBuilder<ReviewDbContext>(Options).AddInterceptors(new FailArtifactInsert());
        await Assert.ThrowsAsync<DbUpdateException>(() => new PostgresReviewStore(builder.Options)
            .PublishAsync(review.Id, 1, ReviewSamples.Request, ReviewSamples.Result));
        Assert.Equal(review, await store.GetCaseAsync(review.ProjectId, review.Id));
        Assert.Null(await store.GetArtifactAsync(review.ProjectId, review.Id, 2));
        await using var db = new ReviewDbContext(Options);
        Assert.Single(await db.PublicationEvents.Where(x => x.CaseId == review.Id).ToListAsync());
    }

    private sealed class FailArtifactInsert : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO review.\"ArtifactVersions\""))
                throw new InvalidOperationException("synthetic_persistence_failure");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task RestrictedRole_CannotAlterHistoryOrApprove()
    {
        var store = new PostgresReviewStore(Options);
        var review = await store.CreateAsync(ReviewSamples.Request, ReviewSamples.Result);
        await using var db = new ReviewDbContext(Options);
        var commands = new[]
        {
            "UPDATE review.\"ArtifactVersions\" SET \"ContentHash\" = 'invalid' WHERE \"CaseId\" = @id",
            "DELETE FROM review.\"PublicationEvents\" WHERE \"CaseId\" = @id",
            "TRUNCATE review.\"PublicationEvents\"",
            "ALTER TABLE review.\"ArtifactVersions\" DISABLE TRIGGER ALL"
        };
        foreach (var sql in commands)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, new NpgsqlParameter("id", review.Id)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        var approval = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE review.\"ReviewCases\" SET \"State\" = 'Approved' WHERE \"Id\" = @id", new NpgsqlParameter("id", review.Id)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, approval.SqlState);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task DatabaseRejectsIncorrectHashAndMissingCurrentArtifact()
    {
        var store = new PostgresReviewStore(Options);
        var review = await store.CreateAsync(ReviewSamples.Request, ReviewSamples.Result);
        await using var db = new ReviewDbContext(Options);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO review.\"ArtifactVersions\" (\"CaseId\",\"Version\",\"WorkflowId\",\"Content\",\"ContentHash\",\"CreatedAt\") VALUES (@id,2,@run,@bytes,'wrong',now())",
            new NpgsqlParameter("id", review.Id), new NpgsqlParameter("run", Guid.NewGuid()), new NpgsqlParameter("bytes", new byte[] { 1 })));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        var missing = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE review.\"ReviewCases\" SET \"CurrentVersion\" = 99 WHERE \"Id\" = @id", new NpgsqlParameter("id", review.Id)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, missing.SqlState);
    }
}
