using Factory.Core.Lifecycle;
using Factory.Core.Reviews;
using Factory.Infrastructure.Reviews;
using Factory.Tests.Reviews;
using Npgsql;

namespace Factory.Tests.Lifecycle;

[Collection("Lifecycle database")]
public class LifecyclePublicationRegressionTests
{
    [LifecycleTheory]
    [InlineData(null)] [InlineData(9L)]
    public async Task PublishedResultRequiresExactNonNullRevision(long? revision)
    {
        await using var f = await LifecycleFixture.Create(); var a = await f.Store.AdmitAsync(f.Actor, f.Command, f.Token);
        await using var db = new NpgsqlConnection(LifecycleFixture.Owner); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await using var update = new NpgsqlCommand("""
            UPDATE lifecycle."GenerationCommands" SET "Status"='Published',"HttpStatus"=201,"ContentHash"=repeat('a',64),"ResultRevision"=@revision WHERE "RunId"=@run
            """, db, tx);
        update.Parameters.AddWithValue("run", a.Outcome.RunId);
        update.Parameters.Add(new("revision", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)revision ?? DBNull.Value });
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync())).SqlState);
        await tx.CommitAsync(); // An aborted PostgreSQL transaction rolls back instead of committing invalid rows.
        Assert.Equal(GenerationStatus.Running, (await f.Store.GetRunAsync(f.Actor, f.Project, a.Outcome.RunId)).Status);
    }

    [LifecycleTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task FirstPublishedCommandWithoutCaseIsRejected(bool immediate)
    {
        await using var f = await LifecycleFixture.Create(); var a = await f.Store.AdmitAsync(f.Actor, f.Command, f.Token);
        await using var db = new NpgsqlConnection(LifecycleFixture.Owner); await db.OpenAsync(); await using var tx = await db.BeginTransactionAsync();
        await using var write = new NpgsqlCommand("""
            SET LOCAL ROLE factory_lifecycle_guard;
            UPDATE lifecycle."GenerationCommands" SET "Status"='Published',"HttpStatus"=201,"ContentHash"=repeat('a',64),"ResultRevision"=1 WHERE "RunId"=@run;
            UPDATE lifecycle."WorkItems" SET "ActiveRunId"=NULL,"Revision"="Revision"+1 WHERE "Id"=@work;
            """, db, tx);
        write.Parameters.AddWithValue("run", a.Outcome.RunId); write.Parameters.AddWithValue("work", a.Outcome.WorkItemId);
        await write.ExecuteNonQueryAsync();
        if (immediate)
        {
            await using var check = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", db, tx);
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => check.ExecuteNonQueryAsync())).SqlState);
            await tx.RollbackAsync();
        }
        else Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync())).SqlState);
        Assert.Null((await f.Store.GetWorkItemAsync(f.Actor, f.Project, a.Outcome.WorkItemId)).CaseId);
    }

    [LifecycleTheory]
    [InlineData("PendingReview", false)] [InlineData("PendingReview", true)]
    [InlineData("Approved", false)] [InlineData("Approved", true)]
    [InlineData("Rejected", false)] [InlineData("Rejected", true)]
    [InlineData("ChangesRequested", false)] [InlineData("ChangesRequested", true)]
    public async Task LaterPublicationWithoutCaseAdvanceIsRejected(string state, bool immediate)
    {
        await using var f = await LifecycleFixture.Create();
        var (_, _, published) = await f.Publish();
        if (state != "PendingReview")
            await f.Decisions.DecideAsync(f.Actor, f.Project, published.Publication!.CaseId,
                new(1, 1, published.Publication.ContentHash, state switch
                { "Approved" => ReviewDecision.Approve, "Rejected" => ReviewDecision.Reject, _ => ReviewDecision.RequestChanges }, "explanation", Guid.NewGuid()));
        var work = await f.Store.GetWorkItemAsync(f.Actor, f.Project, published.WorkItemId);
        var command = f.Command with { WorkItemId = work.WorkItemId, ExpectedETag = work.ETag };
        var a = await f.Store.AdmitAsync(f.Actor, command, f.Token);
        var artifact = ReviewArtifactSerializer.Create(a.CaseId, a.NextVersion, command.Request,
            ReviewSamples.Result with { WorkflowId = a.Outcome.RunId }, DateTimeOffset.UtcNow);
        await using var db = new NpgsqlConnection(LifecycleFixture.Owner); await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();
        await using var sql = new NpgsqlCommand("""
            SET LOCAL ROLE factory_lifecycle_guard;
            INSERT INTO review."ArtifactVersions" ("CaseId","Version","WorkflowId","Content","ContentHash","CreatedAt")
                VALUES(@case,@version,@run,@bytes,@hash,@created);
            INSERT INTO review."PublicationEvents" ("Id","CaseId","Version","ContentHash","CreatedAt")
                VALUES(gen_random_uuid(),@case,@version,@hash,@created);
            UPDATE lifecycle."GenerationCommands" SET "Status"='Published',"HttpStatus"=200,"ContentHash"=@hash,"ResultRevision"=@revision WHERE "RunId"=@run;
            UPDATE lifecycle."WorkItems" SET "ActiveRunId"=NULL,"Revision"="Revision"+1 WHERE "Id"=@work;
            """, db, tx);
        sql.Parameters.AddWithValue("case", a.CaseId); sql.Parameters.AddWithValue("version", a.NextVersion);
        sql.Parameters.AddWithValue("run", a.Outcome.RunId); sql.Parameters.AddWithValue("bytes", artifact.GetContent());
        sql.Parameters.AddWithValue("hash", artifact.ContentHash); sql.Parameters.AddWithValue("created", ReviewArtifactSerializer.Read(artifact).CreatedAt);
        sql.Parameters.AddWithValue("revision", a.ExpectedRevision + 1); sql.Parameters.AddWithValue("work", work.WorkItemId);
        await sql.ExecuteNonQueryAsync();
        if (immediate)
        {
            await using var check = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", db, tx);
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => check.ExecuteNonQueryAsync())).SqlState);
            await tx.RollbackAsync();
        }
        else Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync())).SqlState);
        var after = await f.Store.GetWorkItemAsync(f.Actor, f.Project, work.WorkItemId);
        Assert.Equal(work.CaseRevision, after.CaseRevision); Assert.Equal(work.ArtifactVersion, after.ArtifactVersion);
        Assert.Equal(GenerationStatus.Running, after.LatestStatus);
        // A correct publication and subsequent decision must not revalidate historical commands against current state.
        var next = await f.Store.PublishAsync(f.Actor, command, a, f.Token, ReviewSamples.Result with { WorkflowId = a.Outcome.RunId });
        await f.Decisions.DecideAsync(f.Actor, f.Project, a.CaseId,
            new(next.Publication!.Revision, a.NextVersion, next.Publication.ContentHash, ReviewDecision.Approve, null, Guid.NewGuid()));
        Assert.Equal(published, await f.Store.GetRunAsync(f.Actor, f.Project, published.RunId));
    }
}
