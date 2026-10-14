using System.Text.Json;
using Factory.Core.Authorization;
using Factory.Core.Lifecycle;
using Factory.Infrastructure.Reviews;
using Factory.Tests.Reviews;
using Npgsql;

namespace Factory.Tests.Lifecycle;

[Collection("Lifecycle database")]
public class LifecycleIntegrityTests
{
    [LifecycleFact]
    public async Task DeferredGuardsRejectIncompleteManagedPublicationsAtCommit()
    {
        foreach(var missing in new[]{"lineage","event","command","artifact","run-binding","input-binding","command-hash","case-version"})
        {
            await using var f=await LifecycleFixture.Create();var command=f.Command;var a=await f.Store.AdmitAsync(f.Actor,command,f.Token);
            var artifact=ReviewArtifactSerializer.Create(a.CaseId,1,missing=="input-binding"?command.Request with {Context="different"}:command.Request,
                ReviewSamples.Result with {WorkflowId=a.Outcome.RunId},DateTimeOffset.UtcNow);
            await using var db=new NpgsqlConnection(LifecycleFixture.Owner);await db.OpenAsync();await using var tx=await db.BeginTransactionAsync();
            // Owner-only adversarial probe: runtime cannot SET ROLE (separately tested). All guards stay enabled.
            await using(var role=new NpgsqlCommand("SET LOCAL ROLE factory_lifecycle_guard",db,tx))await role.ExecuteNonQueryAsync();
            var sql="""
                INSERT INTO review."ReviewCases" ("Id","ProjectId","CurrentVersion","Revision","State") VALUES(@case,@project,1,1,'PendingReview');
                """;
            if(missing!="lineage")sql+="""
                INSERT INTO review."ReviewCaseLineage" VALUES(@case,@work,@project);
                """;
            if(missing!="artifact")sql+="""
                INSERT INTO review."ArtifactVersions" ("CaseId","Version","WorkflowId","Content","ContentHash","CreatedAt") VALUES(@case,@version,@artifactRun,@bytes,@hash,@created);
                """;
            if(missing!="event" && missing!="artifact")sql+="""
                INSERT INTO review."PublicationEvents" ("Id","CaseId","Version","ContentHash","CreatedAt") VALUES(gen_random_uuid(),@case,@version,@hash,@created);
                """;
            if(missing!="command")sql+="""
                UPDATE lifecycle."GenerationCommands" SET "Status"='Published',"HttpStatus"=201,"ContentHash"=@commandHash,"ResultRevision"=1 WHERE "RunId"=@run;
                UPDATE lifecycle."WorkItems" SET "ActiveRunId"=NULL,"Revision"="Revision"+1 WHERE "Id"=@work;
                """;
            await using(var write=new NpgsqlCommand(sql,db,tx))
            {
                write.Parameters.AddWithValue("case",a.CaseId);write.Parameters.AddWithValue("project",f.Project);write.Parameters.AddWithValue("work",a.Outcome.WorkItemId);
                write.Parameters.AddWithValue("run",a.Outcome.RunId);write.Parameters.AddWithValue("artifactRun",missing=="run-binding"?Guid.NewGuid():a.Outcome.RunId);
                write.Parameters.AddWithValue("bytes",artifact.GetContent());write.Parameters.AddWithValue("hash",artifact.ContentHash);
                write.Parameters.AddWithValue("commandHash",missing=="command-hash"?new string('b',64):artifact.ContentHash);
                write.Parameters.AddWithValue("version",missing=="case-version"?2:1);
                write.Parameters.AddWithValue("created",ReviewArtifactSerializer.Read(artifact).CreatedAt);
                await write.ExecuteNonQueryAsync(); // Prove statement execution succeeds; enforcement is at COMMIT.
            }
            var error=await Assert.ThrowsAsync<PostgresException>(()=>tx.CommitAsync());Assert.Contains(error.SqlState,new[]{"23514","23503"});
            var work=await f.Store.GetWorkItemAsync(f.Actor,f.Project,a.Outcome.WorkItemId);Assert.Null(work.CaseId);Assert.Equal(1,work.Revision);
        }
    }

    [LifecycleFact]
    public async Task TerminalCommandsAndManagedCasesRejectLegacyBypasses()
    {
        await using var f=await LifecycleFixture.Create();var (_,_,published)=await f.Publish();
        Assert.Equal("23514",(await Assert.ThrowsAsync<PostgresException>(()=>f.Sql("""
            UPDATE lifecycle."GenerationCommands" SET "Status"='Running',"HttpStatus"=202,"ContentHash"=NULL,"ResultRevision"=NULL WHERE "RunId"=@run
            """,new NpgsqlParameter("run",published.RunId)))).SqlState);
        Assert.Equal("23514",(await Assert.ThrowsAsync<PostgresException>(()=>f.Sql("""
            UPDATE review."ReviewCases" SET "ManagementMode"='Legacy',"Revision"="Revision"+1 WHERE "Id"=@case
            """,new NpgsqlParameter("case",published.Publication!.CaseId)))).SqlState);
        var writer=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FACTORY_REVIEW_TEST_CONNECTION")){Database=new NpgsqlConnectionStringBuilder(LifecycleFixture.Owner).Database};
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(()=>new PostgresReviewStore(LifecycleFixture.Options(writer.ConnectionString))
            .PublishAsync(published.Publication!.CaseId,1,ReviewSamples.Request with {ProjectId=f.Project},ReviewSamples.Result));
    }

    [LifecycleFact]
    public async Task ExpiredAttemptIsAbandonedAndLateFinalizationIsFenced()
    {
        await using var f=await LifecycleFixture.Create();var command=f.Command;var old=await f.Store.AdmitAsync(f.Actor,command,f.Token);
        // Test-only clock fixture adjustment by database owner, atomically restoring the transition guard.
        // Runtime cannot ALTER TABLE; the production deadline is never caller configurable.
        await f.Sql("""
            BEGIN;
            ALTER TABLE lifecycle."GenerationCommands" DISABLE TRIGGER command_transition;
            UPDATE lifecycle."GenerationCommands" SET "Deadline"=clock_timestamp()-interval '1 second' WHERE "RunId"=@run;
            SET CONSTRAINTS ALL IMMEDIATE;
            ALTER TABLE lifecycle."GenerationCommands" ENABLE TRIGGER command_transition;
            COMMIT;
            """,new NpgsqlParameter("run",old.Outcome.RunId));
        var snapshot=await f.Store.GetWorkItemAsync(f.Actor,f.Project,old.Outcome.WorkItemId);
        var next=command with {Key=Guid.NewGuid(),WorkItemId=old.Outcome.WorkItemId,ExpectedETag=snapshot.ETag};
        var admitted=await f.Store.AdmitAsync(f.Actor,next,f.Token);
        Assert.NotEqual(old.Outcome.RunId,admitted.Outcome.RunId);
        Assert.Equal(GenerationStatus.Abandoned,(await f.Store.GetRunAsync(f.Actor,f.Project,old.Outcome.RunId)).Status);
        Assert.Equal(LifecycleError.Conflict,(await Assert.ThrowsAsync<LifecycleException>(()=>f.Store.PublishAsync(f.Actor,command,old,f.Token,
            ReviewSamples.Result with {WorkflowId=old.Outcome.RunId}))).Error);
        var success=await f.Store.PublishAsync(f.Actor,next,admitted,f.Token,ReviewSamples.Result with {WorkflowId=admitted.Outcome.RunId});
        Assert.Equal(1,success.Publication!.Version);
        var current=await f.Store.GetWorkItemAsync(f.Actor,f.Project,old.Outcome.WorkItemId);
        Assert.Null(current.ActiveRunId);Assert.Equal(admitted.Outcome.RunId,current.LatestRunId);Assert.NotEqual(snapshot.ETag,current.ETag);
    }

    [LifecycleFact]
    public async Task RuntimeFunctionOwnershipAndSearchPathsAreRestricted()
    {
        await using var db=new NpgsqlConnection(LifecycleFixture.Owner);await db.OpenAsync();
        await using var query=new NpgsqlCommand("""
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace JOIN pg_roles r ON r.oid=p.proowner
            WHERE n.nspname='lifecycle' AND (r.rolname<>'factory_lifecycle_guard' OR r.rolcanlogin OR r.rolsuper OR
                NOT ('search_path=pg_catalog, pg_temp'=ANY(p.proconfig)) OR EXISTS
                (SELECT 1 FROM aclexplode(COALESCE(p.proacl,acldefault('f',p.proowner))) x WHERE grantee=0 AND privilege_type='EXECUTE'))
            """,db);
        Assert.Equal(0L,await query.ExecuteScalarAsync());
    }
}
