using System.Text.Json;
using System.Text.Json.Serialization;
using Factory.Core.Agents;
using Factory.Core.Analysis;
using Factory.Core.Architecture;
using Factory.Core.Authorization;
using Factory.Core.Lifecycle;
using Factory.Core.Orchestration;
using Factory.Core.Workflows;
using Factory.Infrastructure.Reviews;
using Npgsql;
using NpgsqlTypes;

namespace Factory.Infrastructure.Orchestration;

// One instance per database role: the API uses the lifecycle runtime (submit/read only);
// the worker uses the orchestration worker role (claim/renew/complete/fail/publish only).
public sealed class PostgresOrchestrationStore(string connection) : IWorkflowRunStore, IStepQueue
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task<RunSubmission> SubmitAsync(TrustedIdentity actor, Guid key, WorkflowRequest request, CancellationToken ct = default)
    {
        // Same input bounds and canonical sources as M6 admission.
        try { GenerationFingerprint.Validate(new GenerationCommand(key, null, null, DurableWorkflowLimits.WorkflowVersion, request)); }
        catch (LifecycleException) { throw new OrchestrationException(OrchestrationError.InvalidCommand); }
        return Read<RunSubmission>("submit", actor, request.ProjectId, ct, P("key", key), P("version", DurableWorkflowLimits.WorkflowVersion),
            P("input", JsonSerializer.Serialize(request, Json), NpgsqlDbType.Jsonb));
    }

    public Task<RunView> GetRunAsync(TrustedIdentity actor, string projectId, Guid runId, CancellationToken ct = default)
        => Read<RunView>("read_run", actor, projectId, ct, P("run", runId));

    public Task<RunView> CancelAsync(TrustedIdentity actor, string projectId, Guid runId, CancellationToken ct = default)
        => Read<RunView>("cancel", actor, projectId, ct, P("run", runId));

    public Task<RunView> DecideAsync(TrustedIdentity actor, string projectId, Guid runId, Guid decisionId, string choice, CancellationToken ct = default)
    {
        if (!DecisionCodes.IsCode(choice)) throw new OrchestrationException(OrchestrationError.InvalidCommand);
        return Read<RunView>("decide", actor, projectId, ct, P("run", runId), P("decision", decisionId), P("choice", choice));
    }

    public async Task<Guid> RequestDecisionAsync(Guid attemptId, byte[] leaseToken, string reason, IReadOnlyList<string> options, CancellationToken ct = default)
    {
        if (!DecisionCodes.IsCode(reason) || !DecisionCodes.AreValidOptions(options)) throw new OrchestrationException(OrchestrationError.InvalidCommand);
        var id = await Call("SELECT orchestration.request_decision(@attempt,@token,@reason,@options)::text", ct,
            P("attempt", attemptId), P("token", leaseToken), P("reason", reason), P("options", options.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text));
        return Guid.TryParse(id, out var decision) ? decision : throw new OrchestrationException(OrchestrationError.Integrity);
    }

    public async Task<ClaimedStep?> ClaimAsync(string workerId, byte[] leaseToken, CancellationToken ct = default)
    {
        var json = await Call("SELECT orchestration.claim(@worker,@token)::text", ct, P("worker", workerId), P("token", leaseToken));
        if (json is null) return null;
        try
        {
            var raw = JsonSerializer.Deserialize<RawClaim>(json, Json) ?? throw new OrchestrationException(OrchestrationError.Integrity);
            var request = raw.Request.Deserialize<WorkflowRequest>(Json) ?? throw new OrchestrationException(OrchestrationError.Integrity);
            AnalysisResult? analysis = null; ArchitectureProposal? architecture = null;
            // Checkpoints are revalidated against the stored request before any later step uses them.
            if (raw.Analysis is { ValueKind: JsonValueKind.Object } a)
            {
                analysis = a.Deserialize<AnalysisResult>(ArchitectureJson.Options);
                if (!AnalysisValidator.IsValid(analysis, request.ConfirmedRequirements)) throw new OrchestrationException(OrchestrationError.Integrity);
            }
            if (raw.Architecture is { ValueKind: JsonValueKind.Object } b)
            {
                architecture = b.Deserialize<ArchitectureProposal>(ArchitectureJson.Options);
                if (!ArchitectureValidator.IsValid(architecture, Agent(request, analysis))) throw new OrchestrationException(OrchestrationError.Integrity);
            }
            return new ClaimedStep(raw.RunId, raw.AttemptId, raw.Ordinal, raw.Kind, raw.CaseId, request, raw.Steps, analysis, architecture, raw.Decisions);
        }
        catch (JsonException) { throw new OrchestrationException(OrchestrationError.Integrity); }
    }

    public Task RenewAsync(Guid attemptId, byte[] leaseToken, CancellationToken ct = default)
        => Call("SELECT orchestration.renew(@attempt,@token)::text", ct, P("attempt", attemptId), P("token", leaseToken));

    public Task CompleteAnalysisAsync(Guid attemptId, byte[] leaseToken, AnalysisResult analysis, CancellationToken ct = default)
        => Complete(attemptId, leaseToken, JsonSerializer.Serialize(analysis, ArchitectureJson.Options), ct);

    public Task CompleteArchitectureAsync(Guid attemptId, byte[] leaseToken, ArchitectureProposal architecture, CancellationToken ct = default)
        => Complete(attemptId, leaseToken, JsonSerializer.Serialize(architecture, ArchitectureJson.Options), ct);

    public Task FailAsync(Guid attemptId, byte[] leaseToken, string failureCode, CancellationToken ct = default)
        => Call("SELECT orchestration.fail(@attempt,@token,@failure)::text", ct, P("attempt", attemptId), P("token", leaseToken), P("failure", failureCode));

    public async Task<RunPublication> PublishAsync(ClaimedStep step, byte[] leaseToken, CancellationToken ct = default)
    {
        if (step.Kind != StepKind.Publication || step.Analysis is null || step.Architecture is null)
            throw new OrchestrationException(OrchestrationError.Integrity);
        var result = new WorkflowResult(step.RunId, WorkflowStatus.AwaitingHumanReview, WorkflowStageStatus.Completed,
            WorkflowStageStatus.Completed, step.Analysis, step.Architecture);
        Factory.Core.Reviews.ReviewArtifact artifact;
        try { artifact = ReviewArtifactSerializer.Create(step.CaseId, 1, step.Request, result, DateTimeOffset.UtcNow); }
        catch (Exception ex) when (ex is ArgumentException or Factory.Core.Reviews.ReviewIntegrityException)
        { throw new OrchestrationException(OrchestrationError.Integrity); }
        _ = Factory.Core.Reviews.ReviewCase.Create(step.CaseId, step.Request.ProjectId, artifact.ContentHash);
        var json = await Call("SELECT orchestration.publish(@attempt,@token,@bytes)::text", ct,
            P("attempt", step.AttemptId), P("token", leaseToken), P("bytes", artifact.GetContent()));
        return JsonSerializer.Deserialize<RunPublication>(json!, Json) ?? throw new OrchestrationException(OrchestrationError.Integrity);
    }

    private Task Complete(Guid attemptId, byte[] leaseToken, string output, CancellationToken ct)
        => Call("SELECT orchestration.complete(@attempt,@token,@output)::text", ct, P("attempt", attemptId), P("token", leaseToken), P("output", output, NpgsqlDbType.Jsonb));

    private async Task<T> Read<T>(string function, TrustedIdentity actor, string projectId, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        // Function and parameter names are internal constants, never caller supplied.
        var names = new[] { "project", "issuer", "subject", "kind" }.Concat(parameters.Select(p => p.ParameterName));
        var all = new[] { P("project", projectId), P("issuer", actor.Issuer), P("subject", actor.Subject), P("kind", actor.Kind.ToString()) }.Concat(parameters).ToArray();
        var json = await Call($"SELECT orchestration.{function}({string.Join(',', names.Select(n => '@' + n))})::text", ct, all);
        try { return JsonSerializer.Deserialize<T>(json ?? "null", Json) ?? throw new OrchestrationException(OrchestrationError.Integrity); }
        catch (JsonException) { throw new OrchestrationException(OrchestrationError.Integrity); }
    }

    private async Task<string?> Call(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        if (string.IsNullOrWhiteSpace(connection)) throw new OrchestrationException(OrchestrationError.OutcomeUnknown);
        await using var db = new NpgsqlConnection(connection);
        try
        {
            await db.OpenAsync(ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            var commitStarted = false; var commitAcknowledged = false;
            try
            {
                await using (var limits = new NpgsqlCommand("SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='15s'; SET LOCAL idle_in_transaction_session_timeout='15s'", db, tx))
                    await limits.ExecuteNonQueryAsync(ct);
                await using var command = new NpgsqlCommand(sql, db, tx);
                command.Parameters.AddRange(parameters);
                var value = await command.ExecuteScalarAsync(ct);
                // Success is not returned before all deferred checks execute at COMMIT.
                commitStarted = true;
                await tx.CommitAsync(ct);
                commitAcknowledged = true;
                return value as string;
            }
            catch (PostgresException ex)
            {
                if (commitAcknowledged) throw new OrchestrationException(OrchestrationError.OutcomeUnknown);
                var rolledBack = false;
                if (commitStarted && (ex.SqlState.StartsWith("23", StringComparison.Ordinal) || ex.SqlState is "40P01" or "40001")) rolledBack = true;
                else
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await tx.RollbackAsync(cleanup.Token); rolledBack = true; }
                    catch (Exception failure) when (failure is NpgsqlException or OperationCanceledException or InvalidOperationException) { }
                }
                if (!rolledBack) throw new OrchestrationException(OrchestrationError.OutcomeUnknown);
                throw new OrchestrationException(ex.SqlState switch
                {
                    "40P01" or "55P03" or "57014" or "40001" => OrchestrationError.TransactionAborted,
                    "42501" => OrchestrationError.Forbidden, "P6004" => OrchestrationError.NotFound,
                    "P6009" => OrchestrationError.Conflict, "P6029" => OrchestrationError.Capacity,
                    "P7010" => OrchestrationError.LeaseLost, "22023" => OrchestrationError.InvalidCommand,
                    var code when code.StartsWith("23", StringComparison.Ordinal) || code.StartsWith("22", StringComparison.Ordinal) => OrchestrationError.Integrity,
                    _ => OrchestrationError.OutcomeUnknown
                });
            }
        }
        catch (NpgsqlException) { throw new OrchestrationException(OrchestrationError.OutcomeUnknown); }
        catch (OperationCanceledException) { throw new OrchestrationException(OrchestrationError.OutcomeUnknown); }
    }

    private static AgentRequest Agent(WorkflowRequest request, AnalysisResult? analysis)
        => new(request.ProjectId, request.Task, request.Context, request.ConfirmedRequirements, analysis);

    private static NpgsqlParameter P(string name, object? value, NpgsqlDbType? type = null)
    {
        var parameter = new NpgsqlParameter { ParameterName = name, Value = value ?? DBNull.Value };
        if (type is { } dbType) parameter.NpgsqlDbType = dbType;
        return parameter;
    }

    private sealed record RawClaim(Guid RunId, Guid AttemptId, int Ordinal, StepKind Kind, Guid CaseId, JsonElement Request,
        IReadOnlyList<WorkflowStep> Steps, JsonElement? Analysis, JsonElement? Architecture, IReadOnlyList<StepDecision>? Decisions);
}
