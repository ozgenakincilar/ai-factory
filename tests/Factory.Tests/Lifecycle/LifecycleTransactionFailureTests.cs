using Factory.Core.Lifecycle;
using Factory.Infrastructure.Lifecycle;
using Factory.Tests.Reviews;
using Npgsql;

namespace Factory.Tests.Lifecycle;

[Collection("Lifecycle database")]
public class LifecycleTransactionFailureTests
{
    [LifecycleTheory]
    [InlineData("40P01")] [InlineData("55P03")] [InlineData("57014")] [InlineData("40001")]
    public async Task ExplicitServerAbortIsConfirmedBeforePublication(string state)
    {
        await using var f = await LifecycleFixture.Create(); var command = f.Command;
        var a = await f.Store.AdmitAsync(f.Actor, command, f.Token);
        await using var other = new NpgsqlConnection(LifecycleFixture.Owner); await other.OpenAsync();
        await using var otherTx = await other.BeginTransactionAsync();
        var key = Random.Shared.NextInt64(1000000, long.MaxValue - 1);
        await Sql(other, otherTx, $"SET LOCAL deadlock_timeout='10s'; SELECT pg_advisory_xact_lock({key})");
        Task? pending = null;
        var store = new PostgresLifecycleStore(LifecycleFixture.Runtime)
        {
            TransactionObserver = async (phase, db, tx) =>
            {
                if (phase != "before_execute") return;
                if (state == "55P03") await Sql(db, tx, $"SET LOCAL lock_timeout='100ms'; SELECT pg_advisory_xact_lock({key})");
                if (state == "57014") await Sql(db, tx, "SET LOCAL statement_timeout='100ms'; SELECT pg_sleep(5)");
                // Controlled server rejection, not a claim of reproducing a serialization anomaly.
                if (state == "40001") await Sql(db, tx, "DO $$ BEGIN RAISE EXCEPTION 'injected_abort' USING ERRCODE='40001'; END $$");
                if (state == "40P01")
                {
                    await Sql(db, tx, $"SELECT pg_advisory_xact_lock({key + 1})");
                    pending = Sql(other, otherTx, $"SELECT pg_advisory_xact_lock({key + 1})");
                    await WaitForLock(other.ProcessID);
                    await Sql(db, tx, $"SELECT pg_advisory_xact_lock({key})");
                }
            }
        };
        var error = await Assert.ThrowsAsync<LifecycleException>(() => store.PublishAsync(f.Actor, command, a, f.Token,
            ReviewSamples.Result with { WorkflowId = a.Outcome.RunId }));
        Assert.Equal(LifecycleError.TransactionAborted, error.Error);
        if (pending is not null) await pending;
        await otherTx.RollbackAsync();
        var work = await f.Store.GetWorkItemAsync(f.Actor, f.Project, a.Outcome.WorkItemId);
        Assert.Null(work.CaseId); Assert.Equal(GenerationStatus.Running, work.LatestStatus);
        await f.Store.FinishAsync(f.Actor, f.Project, a.Outcome.RunId, f.Token, GenerationStatus.PublicationFailed, "publication_failed");
        Assert.Equal(GenerationStatus.PublicationFailed, (await f.Store.GetRunAsync(f.Actor, f.Project, a.Outcome.RunId)).Status);
    }

    [LifecycleFact]
    public async Task DeferredCommitRejectionRollsBackWholePublication()
    {
        await using var f = await LifecycleFixture.Create(); var command = f.Command;
        var a = await f.Store.AdmitAsync(f.Actor, command, f.Token);
        // Owner-only fixture corrupts the active pointer after the real publication function.
        // The runtime cannot make this mutation; no trigger is disabled or replaced.
        var store = new PostgresLifecycleStore(LifecycleFixture.Owner)
        {
            TransactionObserver = async (phase, db, tx) =>
            {
                if (phase == "before_commit") await Sql(db, tx,
                    $"UPDATE lifecycle.\"WorkItems\" SET \"ActiveRunId\"='{a.Outcome.RunId}',\"Revision\"=\"Revision\"+1 WHERE \"Id\"='{a.Outcome.WorkItemId}'");
            }
        };
        Assert.Equal(LifecycleError.Integrity, (await Assert.ThrowsAsync<LifecycleException>(() => store.PublishAsync(f.Actor, command, a, f.Token,
            ReviewSamples.Result with { WorkflowId = a.Outcome.RunId }))).Error);
        Assert.Null((await f.Store.GetWorkItemAsync(f.Actor, f.Project, a.Outcome.WorkItemId)).CaseId);
        Assert.Equal(GenerationStatus.Running, (await f.Store.GetRunAsync(f.Actor, f.Project, a.Outcome.RunId)).Status);
    }

    [LifecycleTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConnectionLossBeforeOrAfterCommitNeverInventsFailure(bool committed)
    {
        await using var f = await LifecycleFixture.Create(); var command = f.Command;
        var a = await f.Store.AdmitAsync(f.Actor, command, f.Token);
        var store = new PostgresLifecycleStore(LifecycleFixture.Runtime)
        {
            TransactionObserver = async (phase, db, tx) =>
            {
                if (phase != (committed ? "after_commit" : "before_commit")) return;
                await using var admin = new NpgsqlConnection(LifecycleFixture.Owner); await admin.OpenAsync();
                await using var kill = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", admin);
                kill.Parameters.AddWithValue("pid", db.ProcessID); Assert.True((bool)(await kill.ExecuteScalarAsync())!);
                NpgsqlConnection.ClearPool(db); // Reconcile on a fresh connection, not the deliberately killed pooled socket.
                if (committed) throw new NpgsqlException("simulated_lost_commit_acknowledgment");
            }
        };
        Assert.Equal(LifecycleError.OutcomeUnknown, (await Assert.ThrowsAsync<LifecycleException>(() => store.PublishAsync(f.Actor, command, a, f.Token,
            ReviewSamples.Result with { WorkflowId = a.Outcome.RunId }))).Error);
        var replay = await f.Store.AdmitAsync(f.Actor, command, f.Token);
        Assert.True(replay.Replayed); Assert.Equal(a.Outcome.RunId, replay.Outcome.RunId);
        Assert.Equal(committed ? GenerationStatus.Published : GenerationStatus.Running, replay.Outcome.Status);
        if (committed)
        {
            // Late cleanup cannot overwrite an already committed publication.
            Assert.Equal(replay.Outcome, await f.Store.FinishAsync(f.Actor, f.Project, a.Outcome.RunId, f.Token, GenerationStatus.PublicationFailed, "publication_failed"));
        }
    }

    private static async Task Sql(NpgsqlConnection db, NpgsqlTransaction tx, string sql)
    { await using var command = new NpgsqlCommand(sql, db, tx); await command.ExecuteNonQueryAsync(); }
    private static async Task WaitForLock(int pid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await using var db = new NpgsqlConnection(LifecycleFixture.Owner); await db.OpenAsync(timeout.Token);
        while (true)
        {
            await using var query = new NpgsqlCommand("SELECT wait_event_type='Lock' FROM pg_stat_activity WHERE pid=@pid", db);
            query.Parameters.AddWithValue("pid", pid);
            if (await query.ExecuteScalarAsync(timeout.Token) is true) return;
            await Task.Delay(10, timeout.Token);
        }
    }
}
