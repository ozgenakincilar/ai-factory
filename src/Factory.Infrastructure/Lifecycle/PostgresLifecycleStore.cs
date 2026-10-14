using System.Text.Json;
using System.Text.Json.Serialization;
using Factory.Core.Authorization;
using Factory.Core.Lifecycle;
using Factory.Core.Workflows;
using Factory.Infrastructure.Reviews;
using Npgsql;
using NpgsqlTypes;

namespace Factory.Infrastructure.Lifecycle;

public sealed class PostgresLifecycleStore(string connection) : ILifecycleStore
{
    // Internal fault-injection seam; never configured by API composition or caller input.
    internal Func<string, NpgsqlConnection, NpgsqlTransaction, Task>? TransactionObserver { get; init; }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    public Task<GenerationAdmission> AdmitAsync(TrustedIdentity actor, GenerationCommand command, byte[] executionToken, CancellationToken ct = default)
    {
        GenerationFingerprint.Validate(command);
        return Call<GenerationAdmission>("admit", actor, command.Request.ProjectId, ct,
            P("key", command.Key), P("work", command.WorkItemId, NpgsqlDbType.Uuid), P("tag", command.ExpectedETag, NpgsqlDbType.Text),
            P("version", command.WorkflowVersion), P("input", JsonSerializer.Serialize(command.Request, Json), NpgsqlDbType.Jsonb), P("token", executionToken));
    }

    public async Task<GenerationOutcome> PublishAsync(TrustedIdentity actor, GenerationCommand command, GenerationAdmission admission,
        byte[] executionToken, WorkflowResult result, CancellationToken ct = default)
    {
        if (result.WorkflowId != admission.Outcome.RunId) throw new LifecycleException(LifecycleError.Integrity);
        var artifact = ReviewArtifactSerializer.Create(admission.CaseId, admission.NextVersion, command.Request, result, DateTimeOffset.UtcNow);
        if (admission.ExpectedRevision == 0) _ = Factory.Core.Reviews.ReviewCase.Create(admission.CaseId, command.Request.ProjectId, artifact.ContentHash);
        else
        {
            var current = await GetWorkItemAsync(actor, command.Request.ProjectId, admission.Outcome.WorkItemId, ct);
            if (current.CaseId != admission.CaseId || current.CaseRevision != admission.ExpectedRevision)
                throw new LifecycleException(LifecycleError.StaleRevision);
            var aggregate = Factory.Core.Reviews.ReviewCase.Restore(new(current.CaseId.Value, current.ProjectId,
                current.ArtifactVersion!.Value, current.CaseRevision, current.ReviewState!.Value), current.ContentHash!);
            aggregate.Publish(admission.ExpectedRevision, artifact.ContentHash);
        }
        return await Call<GenerationOutcome>("publish", actor, command.Request.ProjectId, ct,
            P("run", admission.Outcome.RunId), P("token", executionToken), P("bytes", artifact.GetContent()));
    }

    public Task<GenerationOutcome> FinishAsync(TrustedIdentity actor, string projectId, Guid runId, byte[] executionToken,
        GenerationStatus status, string failureCode, CancellationToken ct = default)
        => Call<GenerationOutcome>("finish", actor, projectId, ct, P("run", runId), P("token", executionToken), P("status", status.ToString()), P("failure", failureCode));

    public Task<WorkItemSnapshot> GetWorkItemAsync(TrustedIdentity actor, string projectId, Guid workItemId, CancellationToken ct = default)
        => Call<WorkItemSnapshot>("read_work_item", actor, projectId, ct, P("id", workItemId));
    public Task<GenerationOutcome> GetRunAsync(TrustedIdentity actor, string projectId, Guid runId, CancellationToken ct = default)
        => Call<GenerationOutcome>("read_run", actor, projectId, ct, P("id", runId));

    private async Task<T> Call<T>(string function, TrustedIdentity actor, string projectId, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        if (string.IsNullOrWhiteSpace(connection)) throw new LifecycleException(LifecycleError.OutcomeUnknown);
        await using var db = new NpgsqlConnection(connection);
        try
        {
            await db.OpenAsync(ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            var commitStarted = false;
            var commitAcknowledged = false;
            try
            {
                await using (var limits = new NpgsqlCommand("SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='15s'; SET LOCAL idle_in_transaction_session_timeout='15s'", db, tx))
                    await limits.ExecuteNonQueryAsync(ct);
                if (TransactionObserver is not null) await TransactionObserver("before_execute", db, tx);
                // Function names and parameter names are internal constants, never caller supplied.
                var names = new[] { "project", "issuer", "subject", "kind" }.Concat(parameters.Select(p => p.ParameterName));
                await using var sql = new NpgsqlCommand($"SELECT lifecycle.{function}({string.Join(',', names.Select(n => '@' + n))})::text", db, tx);
                sql.Parameters.AddRange(new[] { P("project", projectId), P("issuer", actor.Issuer), P("subject", actor.Subject), P("kind", actor.Kind.ToString()) });
                sql.Parameters.AddRange(parameters);
                var json = (string)(await sql.ExecuteScalarAsync(ct))!;
                var result = JsonSerializer.Deserialize<T>(json, Json) ?? throw new LifecycleException(LifecycleError.Integrity);
                // Success is not returned before all deferred checks execute.
                if (TransactionObserver is not null) await TransactionObserver("before_commit", db, tx);
                commitStarted = true;
                await tx.CommitAsync(ct);
                commitAcknowledged = true;
                if (TransactionObserver is not null) await TransactionObserver("after_commit", db, tx);
                return result;
            }
            catch (PostgresException ex)
            {
                if (commitAcknowledged) throw new LifecycleException(LifecycleError.OutcomeUnknown);
                var rolledBack = false;
                // An explicit server COMMIT rejection is conclusive for these
                // server errors. A transport exception or cancelled await is not such an acknowledgment.
                if (commitStarted &&
                    (ex.SqlState.StartsWith("23", StringComparison.Ordinal) || ex.SqlState is "40P01" or "40001"))
                    rolledBack = true;
                else
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await tx.RollbackAsync(cleanup.Token); rolledBack = true; }
                    catch (Exception failure) when (failure is NpgsqlException or OperationCanceledException or InvalidOperationException) { }
                }
                if (!rolledBack) throw new LifecycleException(LifecycleError.OutcomeUnknown);
                throw new LifecycleException(ex.SqlState switch
                {
                    "40P01" or "55P03" or "57014" or "40001" => LifecycleError.TransactionAborted,
                    "42501" => LifecycleError.Forbidden, "P6004" => LifecycleError.NotFound,
                    "P6012" => LifecycleError.StaleRevision, "P6009" => LifecycleError.Conflict,
                    "P6010" => LifecycleError.Expired, "P6029" => LifecycleError.Capacity,
                    "22023" => LifecycleError.InvalidCommand,
                    var code when code.StartsWith("23", StringComparison.Ordinal) => LifecycleError.Integrity,
                    var code when function == "publish" && code.StartsWith("22", StringComparison.Ordinal) => LifecycleError.Integrity,
                    _ => LifecycleError.OutcomeUnknown
                });
            }
        }
        catch (NpgsqlException) { throw new LifecycleException(LifecycleError.OutcomeUnknown); }
        catch (OperationCanceledException) { throw new LifecycleException(LifecycleError.OutcomeUnknown); }
    }
    private static NpgsqlParameter P(string name, object? value, NpgsqlDbType? type = null)
    {
        var parameter = new NpgsqlParameter { ParameterName = name, Value = value ?? DBNull.Value };
        if (type is { } dbType) parameter.NpgsqlDbType = dbType;
        return parameter;
    }
}
