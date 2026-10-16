using System.Text.Json;
using System.Text.Json.Serialization;
using Factory.Core.Agents;
using Factory.Core.Analysis;
using Factory.Core.Architecture;
using Factory.Core.Authorization;
using Factory.Core.Decomposition;
using Factory.Core.Lifecycle;
using Factory.Core.Orchestration;
using Factory.Core.Tenancy;
using Factory.Core.Workflows;
using Factory.Infrastructure.Reviews;
using Npgsql;
using NpgsqlTypes;

namespace Factory.Infrastructure.Orchestration;

// One instance per database role: the API uses the lifecycle runtime (submit/read only);
// the worker uses the orchestration worker role (claim/renew/complete/fail/publish only).
public sealed class PostgresOrchestrationStore(string connection) : IWorkflowRunStore, IStepQueue, ITenantUsageStore, IModelRegistryStore
{
    public Task<RegisteredModel> RegisterAsync(TrustedIdentity actor, string organizationId, Guid id, ModelRegistration registration, byte[] encryptedApiKey,
        CancellationToken ct = default)
        => Read<RegisteredModel>("register_model", actor, organizationId, ct, "tenancy", P("id", id), P("name", registration.Name),
            P("protocol", registration.Protocol.ToString()), P("baseurl", registration.BaseUrl), P("model", registration.Model),
            P("ciphertext", encryptedApiKey), P("inputprice", registration.InputMicrosPerMillion, NpgsqlDbType.Bigint),
            P("outputprice", registration.OutputMicrosPerMillion, NpgsqlDbType.Bigint));

    public Task<IReadOnlyList<RegisteredModel>> ListAsync(TrustedIdentity actor, string organizationId, CancellationToken ct = default)
        => Read<IReadOnlyList<RegisteredModel>>("list_models", actor, organizationId, ct, "tenancy");

    public Task<ModelEndpoint> GetEndpointAsync(TrustedIdentity actor, string organizationId, Guid modelId, CancellationToken ct = default)
        => Read<ModelEndpoint>("model_endpoint", actor, organizationId, ct, "tenancy", P("model", modelId));

    public Task<RegisteredModel> RecordVerificationAsync(TrustedIdentity actor, string organizationId, Guid modelId, ModelVerificationStatus status,
        bool? structuredOutput, CancellationToken ct = default)
        => Read<RegisteredModel>("record_verification", actor, organizationId, ct, "tenancy", P("model", modelId), P("status", status.ToString()),
            P("structured", structuredOutput, NpgsqlDbType.Boolean));

    public Task<ProjectModelAssignment> AssignAsync(TrustedIdentity actor, string projectId, Guid? modelId, bool acceptCapabilityRisk,
        CancellationToken ct = default)
        => Read<ProjectModelAssignment>("assign_model", actor, projectId, ct, "tenancy", P("model", modelId, NpgsqlDbType.Uuid),
            P("accept", acceptCapabilityRisk));

    public async Task<ModelEndpoint?> GetModelEndpointAsync(Guid attemptId, byte[] leaseToken, CancellationToken ct = default)
    {
        var json = await Call("SELECT orchestration.model_for_attempt(@attempt,@token)::text", ct, P("attempt", attemptId), P("token", leaseToken));
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<ModelEndpoint>(json, Json) ?? throw new OrchestrationException(OrchestrationError.Integrity); }
        catch (JsonException) { throw new OrchestrationException(OrchestrationError.Integrity); }
    }

    public Task<OrganizationUsage> GetOrganizationUsageAsync(TrustedIdentity actor, string organizationId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(organizationId) || organizationId.Length > 210
            ? throw new OrchestrationException(OrchestrationError.Forbidden)
            : Read<OrganizationUsage>("read_usage", actor, organizationId, ct, "tenancy",
                P("from", from.ToUniversalTime(), NpgsqlDbType.TimestampTz), P("to", to.ToUniversalTime(), NpgsqlDbType.TimestampTz));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task<RunSubmission> SubmitAsync(TrustedIdentity actor, Guid key, WorkflowRequest request, CancellationToken ct = default)
        => SubmitAsync(actor, key, request, WorkflowVersions.AnalysisArchitecture, ct);

    public Task<RunSubmission> SubmitAsync(TrustedIdentity actor, Guid key, WorkflowRequest request, string workflowVersion, CancellationToken ct = default)
    {
        // Same input bounds and canonical sources as M6 admission (checked with the M6 version constant).
        if (!WorkflowVersions.IsKnown(workflowVersion)) throw new OrchestrationException(OrchestrationError.InvalidCommand);
        try { GenerationFingerprint.Validate(new GenerationCommand(key, null, null, DurableWorkflowLimits.WorkflowVersion, request)); }
        catch (LifecycleException) { throw new OrchestrationException(OrchestrationError.InvalidCommand); }
        return Read<RunSubmission>("submit", actor, request.ProjectId, ct, P("key", key), P("version", workflowVersion),
            P("input", JsonSerializer.Serialize(request, Json), NpgsqlDbType.Jsonb));
    }

    public async Task<RunPlan> GetPlanAsync(TrustedIdentity actor, string projectId, Guid runId, CancellationToken ct = default)
    {
        var raw = await Read<RawPlan>("read_plan", actor, projectId, ct, P("run", runId));
        DecompositionPlan? plan;
        try { plan = raw.Plan.Deserialize<DecompositionPlan>(DecompositionJson.Options); }
        catch (JsonException) { throw new OrchestrationException(OrchestrationError.Integrity); }
        if (DecompositionPlanValidator.GetStructureError(plan) is not null) throw new OrchestrationException(OrchestrationError.Integrity);
        // Routing is deterministic, so the popup can show each part's tier before anything runs.
        return new RunPlan(raw.RunId, plan!, plan!.Parts.Select(p =>
        {
            var route = ModelRoutingPolicy.Route(StepKind.Part, p.Complexity, p.Importance);
            return new PartRoute(p.Key, route.Tier, route.Reason);
        }).ToArray(), ModelRoutingPolicy.Version);
    }

    public Task<RunSubmission> SubmitRegenerationAsync(TrustedIdentity actor, Guid key, Guid workItemId, string expectedETag, WorkflowRequest request,
        CancellationToken ct = default)
    {
        // Same canonical bounds and strong-ETag format as the retired M6 regeneration route.
        try { GenerationFingerprint.Validate(new GenerationCommand(key, workItemId, expectedETag, DurableWorkflowLimits.WorkflowVersion, request)); }
        catch (LifecycleException) { throw new OrchestrationException(OrchestrationError.InvalidCommand); }
        return Read<RunSubmission>("submit_regeneration", actor, request.ProjectId, ct, P("key", key), P("work", workItemId), P("tag", expectedETag),
            P("version", DurableWorkflowLimits.WorkflowVersion), P("input", JsonSerializer.Serialize(request, Json), NpgsqlDbType.Jsonb));
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

    public Task AuthorizeWatchAsync(TrustedIdentity actor, string projectId, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(projectId) || projectId.Length > 200
            ? throw new OrchestrationException(OrchestrationError.Forbidden)
            : Read<bool>("authorize_watch", actor, projectId, ct);

    public Task<ProjectUsage> GetUsageAsync(TrustedIdentity actor, string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        => Read<ProjectUsage>("read_usage", actor, projectId, ct,
            P("from", from.ToUniversalTime(), NpgsqlDbType.TimestampTz), P("to", to.ToUniversalTime(), NpgsqlDbType.TimestampTz));

    public Task RecordModelCallAsync(Guid attemptId, byte[] leaseToken, ModelCall call, CancellationToken ct = default)
        => Call("SELECT orchestration.record_model_call(@attempt,@token,@provider,@model,@input,@output,@duration,@outcome,@reservation)::text", ct,
            P("attempt", attemptId), P("token", leaseToken), P("provider", call.Provider, NpgsqlDbType.Text), P("model", call.Model, NpgsqlDbType.Text),
            P("input", call.InputTokens, NpgsqlDbType.Integer), P("output", call.OutputTokens, NpgsqlDbType.Integer),
            P("duration", call.DurationMs), P("outcome", call.Outcome.ToString()), P("reservation", call.ReservationId, NpgsqlDbType.Uuid));

    public Task RecordRouteAsync(Guid attemptId, byte[] leaseToken, ModelRoute route, ModelIdentity? model, CancellationToken ct = default)
        => Call("SELECT orchestration.record_route(@attempt,@token,@tier,@reason,@policy,@provider,@model)::text", ct,
            P("attempt", attemptId), P("token", leaseToken), P("tier", route.Tier.ToString()), P("reason", route.Reason), P("policy", route.PolicyVersion),
            P("provider", model?.Provider, NpgsqlDbType.Text), P("model", model?.Model, NpgsqlDbType.Text));

    public async Task<Guid> ReserveCostAsync(Guid attemptId, byte[] leaseToken, ModelIdentity model, int estimatedInputTokens, int maxOutputTokens,
        CancellationToken ct = default)
    {
        var id = await Call("SELECT orchestration.reserve_cost(@attempt,@token,@provider,@model,@input,@output)::text", ct,
            P("attempt", attemptId), P("token", leaseToken), P("provider", model.Provider), P("model", model.Model),
            P("input", estimatedInputTokens), P("output", maxOutputTokens));
        return Guid.TryParse(id, out var reservation) ? reservation : throw new OrchestrationException(OrchestrationError.Integrity);
    }

    public Task<IReadOnlyList<BudgetStatus>> GetBudgetsAsync(TrustedIdentity actor, string organizationId, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(organizationId) || organizationId.Length > 210
            ? throw new OrchestrationException(OrchestrationError.Forbidden)
            : Read<IReadOnlyList<BudgetStatus>>("read_budgets", actor, organizationId, ct, "tenancy");

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
            if (raw.NextVersion < 1) throw new OrchestrationException(OrchestrationError.Integrity);
            var version = raw.WorkflowVersion ?? WorkflowVersions.AnalysisArchitecture;
            if (!WorkflowVersions.IsKnown(version)) throw new OrchestrationException(OrchestrationError.Integrity);
            DecompositionPlan? plan = null; PlanPart? part = null; Dictionary<string, ArchitectureProposal>? outputs = null;
            if (raw.Plan is { ValueKind: JsonValueKind.Object } c)
            {
                plan = c.Deserialize<DecompositionPlan>(DecompositionJson.Options);
                if (!DecompositionPlanValidator.IsValid(plan, analysis)) throw new OrchestrationException(OrchestrationError.Integrity);
            }
            if (raw.Part is { ValueKind: JsonValueKind.Object } d)
            {
                // The stored part must be exactly the approved plan's part with that key.
                var key = d.TryGetProperty("key", out var k) ? k.GetString() : null;
                part = plan?.Parts.FirstOrDefault(p => p.Key == key) ?? throw new OrchestrationException(OrchestrationError.Integrity);
                if (!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(d.GetRawText()),
                        JsonSerializer.SerializeToNode(part, DecompositionJson.Options)))
                    throw new OrchestrationException(OrchestrationError.Integrity);
            }
            if (raw.PartOutputs is { Count: > 0 } stored)
            {
                if (plan is null) throw new OrchestrationException(OrchestrationError.Integrity);
                outputs = [];
                foreach (var (key, value) in stored)
                {
                    var owner = plan.Parts.FirstOrDefault(p => p.Key == key) ?? throw new OrchestrationException(OrchestrationError.Integrity);
                    var proposal = value.Deserialize<ArchitectureProposal>(ArchitectureJson.Options);
                    if (!ArchitectureValidator.IsValid(proposal, PartScope.Request(Agent(request, analysis), owner, [])))
                        throw new OrchestrationException(OrchestrationError.Integrity);
                    outputs[key] = proposal!;
                }
            }
            return new ClaimedStep(raw.RunId, raw.AttemptId, raw.Ordinal, raw.Kind, raw.CaseId, request, raw.Steps, analysis, architecture, raw.Decisions,
                raw.NextVersion, version, raw.Dependencies, plan, part, outputs);
        }
        catch (JsonException) { throw new OrchestrationException(OrchestrationError.Integrity); }
    }

    public Task RenewAsync(Guid attemptId, byte[] leaseToken, CancellationToken ct = default)
        => Call("SELECT orchestration.renew(@attempt,@token)::text", ct, P("attempt", attemptId), P("token", leaseToken));

    public Task CompleteAnalysisAsync(Guid attemptId, byte[] leaseToken, AnalysisResult analysis, CancellationToken ct = default)
        => Complete(attemptId, leaseToken, JsonSerializer.Serialize(analysis, ArchitectureJson.Options), ct);

    public Task CompleteArchitectureAsync(Guid attemptId, byte[] leaseToken, ArchitectureProposal architecture, CancellationToken ct = default)
        => Complete(attemptId, leaseToken, JsonSerializer.Serialize(architecture, ArchitectureJson.Options), ct);

    public Task CompletePlanningAsync(Guid attemptId, byte[] leaseToken, DecompositionPlan plan, CancellationToken ct = default)
        => Complete(attemptId, leaseToken, JsonSerializer.Serialize(plan, DecompositionJson.Options), ct);

    public Task CompletePartAsync(Guid attemptId, byte[] leaseToken, ArchitectureProposal proposal, CancellationToken ct = default)
        => Complete(attemptId, leaseToken, JsonSerializer.Serialize(proposal, ArchitectureJson.Options), ct);

    public Task ApprovePlanAsync(Guid attemptId, byte[] leaseToken, CancellationToken ct = default)
        => Call("SELECT orchestration.approve_plan(@attempt,@token)::text", ct, P("attempt", attemptId), P("token", leaseToken));

    public Task FailAsync(Guid attemptId, byte[] leaseToken, string failureCode, CancellationToken ct = default)
        => Call("SELECT orchestration.fail(@attempt,@token,@failure)::text", ct, P("attempt", attemptId), P("token", leaseToken), P("failure", failureCode));

    public async Task<RunPublication> PublishAsync(ClaimedStep step, byte[] leaseToken, CancellationToken ct = default)
    {
        if (step.Kind != StepKind.Publication || step.Analysis is null || step.Architecture is null)
            throw new OrchestrationException(OrchestrationError.Integrity);
        var result = new WorkflowResult(step.RunId, WorkflowStatus.AwaitingHumanReview, WorkflowStageStatus.Completed,
            WorkflowStageStatus.Completed, step.Analysis, step.Architecture);
        Factory.Core.Reviews.ReviewArtifact artifact;
        try { artifact = ReviewArtifactSerializer.Create(step.CaseId, step.NextVersion, step.Request, result, DateTimeOffset.UtcNow); }
        catch (Exception ex) when (ex is ArgumentException or Factory.Core.Reviews.ReviewIntegrityException)
        { throw new OrchestrationException(OrchestrationError.Integrity); }
        _ = Factory.Core.Reviews.ReviewCase.Create(step.CaseId, step.Request.ProjectId, artifact.ContentHash);
        var json = await Call("SELECT orchestration.publish(@attempt,@token,@bytes)::text", ct,
            P("attempt", step.AttemptId), P("token", leaseToken), P("bytes", artifact.GetContent()));
        return JsonSerializer.Deserialize<RunPublication>(json!, Json) ?? throw new OrchestrationException(OrchestrationError.Integrity);
    }

    private Task Complete(Guid attemptId, byte[] leaseToken, string output, CancellationToken ct)
        => Call("SELECT orchestration.complete(@attempt,@token,@output)::text", ct, P("attempt", attemptId), P("token", leaseToken), P("output", output, NpgsqlDbType.Jsonb));

    private Task<T> Read<T>(string function, TrustedIdentity actor, string projectId, CancellationToken ct, params NpgsqlParameter[] parameters)
        => Read<T>(function, actor, projectId, ct, "orchestration", parameters);

    // scope is the project or organization id; schema and function names are internal constants.
    private async Task<T> Read<T>(string function, TrustedIdentity actor, string scope, CancellationToken ct, string schema, params NpgsqlParameter[] parameters)
    {
        var names = new[] { "scope", "issuer", "subject", "kind" }.Concat(parameters.Select(p => p.ParameterName));
        var all = new[] { P("scope", scope), P("issuer", actor.Issuer), P("subject", actor.Subject), P("kind", actor.Kind.ToString()) }.Concat(parameters).ToArray();
        var json = await Call($"SELECT {schema}.{function}({string.Join(',', names.Select(n => '@' + n))})::text", ct, all);
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
                    "P7020" => OrchestrationError.BudgetExceeded, "P7021" => OrchestrationError.PriceUnknown,
                    "P6012" => OrchestrationError.StaleRevision, "P7030" => OrchestrationError.CapabilityUnverified,
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
        IReadOnlyList<WorkflowStep> Steps, JsonElement? Analysis, JsonElement? Architecture, IReadOnlyList<StepDecision>? Decisions, int NextVersion,
        string? WorkflowVersion = null, IReadOnlyList<StepDependency>? Dependencies = null, JsonElement? Plan = null, JsonElement? Part = null,
        Dictionary<string, JsonElement>? PartOutputs = null);

    private sealed record RawPlan(Guid RunId, JsonElement Plan);
}
