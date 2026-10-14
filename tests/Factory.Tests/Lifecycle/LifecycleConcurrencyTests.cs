using Factory.Core.Lifecycle;
using Factory.Core.Reviews;
using Factory.Infrastructure.Lifecycle;
using Factory.Infrastructure.Reviews;
using Factory.Tests.Reviews;
using Npgsql;

namespace Factory.Tests.Lifecycle;

[Collection("Lifecycle database")]
public class LifecycleConcurrencyTests
{
    [LifecycleFact]
    public async Task AbandonmentWinsBeforeLateFinalization()
    {
        await using var f=await LifecycleFixture.Create();var command=f.Command;var a=await f.Store.AdmitAsync(f.Actor,command,f.Token);
        await SetDeadline(f,a.Outcome.RunId,-1);
        var work=await f.Store.GetWorkItemAsync(f.Actor,f.Project,a.Outcome.WorkItemId);
        await using var owner=new NpgsqlConnection(LifecycleFixture.Owner);await owner.OpenAsync();await using var tx=await owner.BeginTransactionAsync();
        await using(var hold=new NpgsqlCommand("""SELECT 1 FROM lifecycle."WorkItems" WHERE "Id"=@id FOR UPDATE""",owner,tx))
        {hold.Parameters.AddWithValue("id",work.WorkItemId);await hold.ExecuteScalarAsync();}
        var retryLabel="m6-abandon-"+Guid.NewGuid().ToString("N");var publishLabel="m6-late-"+Guid.NewGuid().ToString("N");
        var retryStore=new PostgresLifecycleStore(new NpgsqlConnectionStringBuilder(LifecycleFixture.Runtime){ApplicationName=retryLabel}.ConnectionString);
        var publisher=new PostgresLifecycleStore(new NpgsqlConnectionStringBuilder(LifecycleFixture.Runtime){ApplicationName=publishLabel}.ConnectionString);
        var retry=retryStore.AdmitAsync(f.Actor,command with {Key=Guid.NewGuid(),WorkItemId=work.WorkItemId,ExpectedETag=work.ETag},f.Token);
        await WaitForLock(retryLabel);
        var late=publisher.PublishAsync(f.Actor,command,a,f.Token,ReviewSamples.Result with {WorkflowId=a.Outcome.RunId});await WaitForLock(publishLabel);
        await tx.CommitAsync();var next=await retry;
        Assert.Equal(LifecycleError.Conflict,(await Assert.ThrowsAsync<LifecycleException>(async()=>await late)).Error);
        Assert.Equal(GenerationStatus.Abandoned,(await f.Store.GetRunAsync(f.Actor,f.Project,a.Outcome.RunId)).Status);
        Assert.Equal(next.Outcome.RunId,(await f.Store.GetWorkItemAsync(f.Actor,f.Project,work.WorkItemId)).ActiveRunId);
    }

    [LifecycleFact]
    public async Task AcceptedFinalizationCanCommitAfterDeadlineAndCannotBeAbandoned()
    {
        await using var f=await LifecycleFixture.Create();var command=f.Command;var a=await f.Store.AdmitAsync(f.Actor,command,f.Token);
        await SetDeadline(f,a.Outcome.RunId,2);
        var work=await f.Store.GetWorkItemAsync(f.Actor,f.Project,a.Outcome.WorkItemId);
        await using var db=new NpgsqlConnection(LifecycleFixture.Runtime);await db.OpenAsync();await using var tx=await db.BeginTransactionAsync();await PublishSql(db,tx,f,command,a);
        await using var clock=new NpgsqlConnection(LifecycleFixture.Owner);await clock.OpenAsync();
        using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while(true)
            {
                await using var query=new NpgsqlCommand("""SELECT clock_timestamp()>"Deadline" FROM lifecycle."GenerationCommands" WHERE "RunId"=@id""",clock);
                query.Parameters.AddWithValue("id",a.Outcome.RunId);if((bool)(await query.ExecuteScalarAsync(timeout.Token))!)break;
                await Task.Delay(10,timeout.Token);
            }
        }
        var label="m6-expired-retry-"+Guid.NewGuid().ToString("N");
        var retryStore=new PostgresLifecycleStore(new NpgsqlConnectionStringBuilder(LifecycleFixture.Runtime){ApplicationName=label}.ConnectionString);
        var retry=retryStore.AdmitAsync(f.Actor,command with {Key=Guid.NewGuid(),WorkItemId=work.WorkItemId,ExpectedETag=work.ETag},f.Token);
        await WaitForLock(label);await tx.CommitAsync();
        Assert.Equal(LifecycleError.StaleRevision,(await Assert.ThrowsAsync<LifecycleException>(async()=>await retry)).Error);
        Assert.Equal(GenerationStatus.Published,(await f.Store.GetRunAsync(f.Actor,f.Project,a.Outcome.RunId)).Status);
    }

    private static Task SetDeadline(LifecycleFixture f,Guid run,int seconds)=>f.Sql("""
        BEGIN;
        ALTER TABLE lifecycle."GenerationCommands" DISABLE TRIGGER command_transition;
        UPDATE lifecycle."GenerationCommands" SET "Deadline"=clock_timestamp()+(@seconds * interval '1 second') WHERE "RunId"=@run;
        SET CONSTRAINTS ALL IMMEDIATE;
        ALTER TABLE lifecycle."GenerationCommands" ENABLE TRIGGER command_transition;
        COMMIT;
        """,new NpgsqlParameter("run",run),new NpgsqlParameter("seconds",seconds));

    [LifecycleTheory]
    [InlineData("membership")][InlineData("project")][InlineData("principal")]
    public async Task RevocationFirstBlocksAndDeniesPublication(string resource)
    {
        await using var f=await LifecycleFixture.Create();var command=f.Command;var a=await f.Store.AdmitAsync(f.Actor,command,f.Token);
        await using var owner=new NpgsqlConnection(LifecycleFixture.Owner);await owner.OpenAsync();await using var tx=await owner.BeginTransactionAsync();
        await using(var revoke=Revoke(owner,tx,f,resource)) await revoke.ExecuteNonQueryAsync();
        var label="m6-revoke-"+Guid.NewGuid().ToString("N");
        var store=new PostgresLifecycleStore(new NpgsqlConnectionStringBuilder(LifecycleFixture.Runtime){ApplicationName=label}.ConnectionString);
        var publish=store.PublishAsync(f.Actor,command,a,f.Token,ReviewSamples.Result with {WorkflowId=a.Outcome.RunId});
        await WaitForLock(label);await tx.CommitAsync();
        Assert.Equal(LifecycleError.Forbidden,(await Assert.ThrowsAsync<LifecycleException>(async()=>await publish)).Error);
        if(resource=="membership") Assert.Null((await f.Store.GetWorkItemAsync(f.Actor,f.Project,a.Outcome.WorkItemId)).CaseId);
        else Assert.Equal(LifecycleError.Forbidden,(await Assert.ThrowsAsync<LifecycleException>(()=>f.Store.GetWorkItemAsync(f.Actor,f.Project,a.Outcome.WorkItemId))).Error);
    }

    [LifecycleTheory]
    [InlineData("membership")][InlineData("project")][InlineData("principal")]
    public async Task PublicationFirstHoldsAuthorizationUntilCommit(string resource)
    {
        await using var f=await LifecycleFixture.Create();var command=f.Command;var a=await f.Store.AdmitAsync(f.Actor,command,f.Token);
        await using var db=new NpgsqlConnection(LifecycleFixture.Runtime);await db.OpenAsync();await using var tx=await db.BeginTransactionAsync();
        await PublishSql(db,tx,f,command,a);
        // Read before queuing a revocation: a later reader can legitimately wait behind
        // the exclusive authorization lock request, which this test itself must release.
        var before=await f.Store.GetWorkItemAsync(f.Actor,f.Project,a.Outcome.WorkItemId);Assert.Null(before.CaseId);Assert.Equal(GenerationStatus.Running,before.LatestStatus);
        var label="m6-after-"+Guid.NewGuid().ToString("N");
        await using var owner=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(LifecycleFixture.Owner){ApplicationName=label}.ConnectionString);await owner.OpenAsync();
        await using var revoke=Revoke(owner,null,f,resource);
        var pending=revoke.ExecuteNonQueryAsync();await WaitForLock(label);
        await tx.CommitAsync();await pending;
        if(resource=="membership")
        {
            var after=await f.Store.GetWorkItemAsync(f.Actor,f.Project,a.Outcome.WorkItemId);Assert.Equal(GenerationStatus.Published,after.LatestStatus);Assert.Null(after.ActiveRunId);
        }
        else
        {
            Assert.Equal(LifecycleError.Forbidden,(await Assert.ThrowsAsync<LifecycleException>(()=>f.Store.GetWorkItemAsync(f.Actor,f.Project,a.Outcome.WorkItemId))).Error);
            await using var query=new NpgsqlCommand("SELECT lifecycle.snapshot(@project,@id)->>'latestStatus'",owner);
            query.Parameters.AddWithValue("project",f.Project);query.Parameters.AddWithValue("id",a.Outcome.WorkItemId);Assert.Equal("Published",await query.ExecuteScalarAsync());
        }
    }

    [LifecycleFact]
    public async Task PublicationFirstMakesConcurrentHumanDecisionStale()
    {
        await using var f=await LifecycleFixture.Create();var (_,_,published)=await f.Publish();var work=await f.Store.GetWorkItemAsync(f.Actor,f.Project,published.WorkItemId);
        var command=f.Command with {WorkItemId=work.WorkItemId,ExpectedETag=work.ETag};var a=await f.Store.AdmitAsync(f.Actor,command,f.Token);
        await using var db=new NpgsqlConnection(LifecycleFixture.Runtime);await db.OpenAsync();await using var tx=await db.BeginTransactionAsync();await PublishSql(db,tx,f,command,a);
        var label="m6-decision-"+Guid.NewGuid().ToString("N");
        var connection=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FACTORY_DECISION_TEST_CONNECTION"))
            {Database=new NpgsqlConnectionStringBuilder(LifecycleFixture.Owner).Database,ApplicationName=label};
        var decisions=new PostgresReviewDecisionStore(LifecycleFixture.Options(connection.ConnectionString));
        var decision=decisions.DecideAsync(f.Actor,f.Project,work.CaseId!.Value,new(1,1,work.ContentHash!,ReviewDecision.Approve,null,Guid.NewGuid()));
        await WaitForLock(label);await tx.CommitAsync();
        Assert.Equal(ReviewError.StaleRevision,(await Assert.ThrowsAsync<ReviewCommandException>(async()=>await decision)).Error);
    }

    [LifecycleFact]
    public async Task PublicationRollbackDoesNotLoseRunningCommandOrExposeArtifact()
    {
        await using var f=await LifecycleFixture.Create();var command=f.Command;var a=await f.Store.AdmitAsync(f.Actor,command,f.Token);
        await using(var db=new NpgsqlConnection(LifecycleFixture.Runtime))
        {
            await db.OpenAsync();await using var tx=await db.BeginTransactionAsync();await PublishSql(db,tx,f,command,a);
            await tx.RollbackAsync();
        }
        var current=await f.Store.GetWorkItemAsync(f.Actor,f.Project,a.Outcome.WorkItemId);
        Assert.Null(current.CaseId);Assert.Equal(GenerationStatus.Running,current.LatestStatus);Assert.Equal(1,current.Revision);
        await f.Store.FinishAsync(f.Actor,f.Project,a.Outcome.RunId,f.Token,GenerationStatus.PublicationFailed,"publication_failed");
        var failed=await f.Store.GetWorkItemAsync(f.Actor,f.Project,a.Outcome.WorkItemId);Assert.Null(failed.ActiveRunId);Assert.Equal(2,failed.Revision);
    }

    private static async Task PublishSql(NpgsqlConnection db,NpgsqlTransaction tx,LifecycleFixture f,GenerationCommand command,GenerationAdmission a)
    {
        var artifact=ReviewArtifactSerializer.Create(a.CaseId,a.NextVersion,command.Request,ReviewSamples.Result with {WorkflowId=a.Outcome.RunId},DateTimeOffset.UtcNow);
        await using var sql=new NpgsqlCommand("SELECT lifecycle.publish(@project,@issuer,@subject,@kind,@run,@token,@bytes)",db,tx);
        sql.Parameters.AddWithValue("project",f.Project);sql.Parameters.AddWithValue("issuer",f.Actor.Issuer);sql.Parameters.AddWithValue("subject",f.Actor.Subject);sql.Parameters.AddWithValue("kind",f.Actor.Kind.ToString());
        sql.Parameters.AddWithValue("run",a.Outcome.RunId);sql.Parameters.AddWithValue("token",f.Token);sql.Parameters.AddWithValue("bytes",artifact.GetContent());await sql.ExecuteScalarAsync();
    }
    private static NpgsqlCommand Revoke(NpgsqlConnection db,NpgsqlTransaction? tx,LifecycleFixture fixture,string resource)
    {
        var sql=resource switch
        {
            "project" => """UPDATE access."Projects" SET "IsActive"=false,"Revision"="Revision"+1 WHERE "Id"=@p""",
            "principal" => """UPDATE access."Principals" SET "IsActive"=false,"Revision"="Revision"+1 WHERE "Id"=@actor""",
            _ => """UPDATE access."ProjectMemberships" SET "IsActive"=false,"Revision"="Revision"+1 WHERE "ProjectId"=@p AND "Role"='Generator'"""
        };
        var command=new NpgsqlCommand(sql,db,tx);command.Parameters.AddWithValue("p",fixture.Project);command.Parameters.AddWithValue("actor",fixture.PrincipalId);return command;
    }
    private static async Task WaitForLock(string application)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(4));await using var db=new NpgsqlConnection(LifecycleFixture.Owner);await db.OpenAsync(deadline.Token);
        while(true)
        {
            await using var query=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=@name AND wait_event_type='Lock')",db);
            query.Parameters.AddWithValue("name",application);if((bool)(await query.ExecuteScalarAsync(deadline.Token))!)return;
            await Task.Delay(10,deadline.Token);
        }
    }
}
