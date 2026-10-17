using System.Security.Cryptography;
using System.Text.Json;
using Factory.Core.Authorization;
using Factory.Core.Deployment;
using Factory.Core.Orchestration;
using NpgsqlTypes;

namespace Factory.Infrastructure.Orchestration;

// M13A.2 deployments: same roles and error mapping as development jobs.
public sealed partial class PostgresOrchestrationStore : IDeploymentJobs, IDeploymentQueue
{
    public Task<DeploymentSubmission> SubmitDeploymentAsync(TrustedIdentity actor, string projectId, Guid key, string targetName, Guid developmentJobId,
        CancellationToken ct = default)
        => Read<DeploymentSubmission>("submit_deployment", actor, Project(projectId), ct, P("key", key), P("target", targetName), P("job", developmentJobId));

    public Task<DeploymentList> ListDeploymentsAsync(TrustedIdentity actor, string projectId, CancellationToken ct = default)
        => Read<DeploymentList>("list_deployments", actor, Project(projectId), ct);

    public Task<DeploymentSummaryView> GetDeploymentAsync(TrustedIdentity actor, string projectId, Guid deploymentId, CancellationToken ct = default)
        => Read<DeploymentSummaryView>("read_deployment", actor, Project(projectId), ct, P("id", deploymentId));

    public Task<DeploymentSummaryView> DecideDeploymentAsync(TrustedIdentity actor, string projectId, Guid deploymentId, bool approve, string bindingSha256,
        bool allowDestructive, CancellationToken ct = default)
        => Read<DeploymentSummaryView>("decide_deployment", actor, Project(projectId), ct, P("id", deploymentId), P("approve", approve),
            P("binding", bindingSha256), P("destructive", allowDestructive));

    public async Task<ClaimedDeployment?> ClaimDeploymentAsync(string workerId, byte[] leaseToken, CancellationToken ct = default)
    {
        var json = await Call("SELECT orchestration.claim_deployment(@worker,@token)::text", ct, P("worker", workerId), P("token", leaseToken));
        if (json is null) return null;
        try
        {
            var raw = JsonSerializer.Deserialize<RawDeploymentClaim>(json, Json) ?? throw new OrchestrationException(OrchestrationError.Integrity);
            var evidence = Convert.FromBase64String(raw.Evidence);
            if (Convert.ToHexStringLower(SHA256.HashData(evidence)) != raw.EvidenceSha256) throw new OrchestrationException(OrchestrationError.Integrity);
            var target = raw.Target.Deserialize<DeploymentTarget>(Json) ?? throw new OrchestrationException(OrchestrationError.Integrity);
            var plan = raw.PlanFile is null ? null : Convert.FromBase64String(raw.PlanFile);
            var phase = raw.Phase == "plan" ? DeploymentPhase.Plan : raw.Phase == "apply" ? DeploymentPhase.Apply
                : throw new OrchestrationException(OrchestrationError.Integrity);
            return new(raw.DeploymentId, raw.ProjectId, phase, raw.TargetName, target, raw.DevelopmentJobId, evidence, raw.EvidenceSha256,
                raw.BindingSha256, raw.Binding?.Deserialize<DeploymentBinding>(Json), plan, raw.Summary?.Deserialize<PlanSummary>(Json), raw.ModuleId,
                raw.ModuleVersion, raw.AllowDestructive, raw.ApprovedBy, raw.ApprovedAt);
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { throw new OrchestrationException(OrchestrationError.Integrity); }
    }

    public Task RenewDeploymentAsync(Guid deploymentId, byte[] leaseToken, CancellationToken ct = default)
        => Call("SELECT orchestration.renew_deployment(@id,@token)::text", ct, P("id", deploymentId), P("token", leaseToken));

    public Task RecordPlanAsync(Guid deploymentId, byte[] leaseToken, DeploymentModule module, ReleasePackage release, DeploymentPlan plan,
        CancellationToken ct = default)
    {
        if (plan.PlanFile.Length is 0 or > DeploymentJobLimits.MaxPlanBytes || plan.Binding.Hash() != plan.BindingSha256)
            throw new OrchestrationException(OrchestrationError.InvalidCommand);
        return Call("SELECT orchestration.record_deployment_plan(@id,@token,@module,@version,@bindingsha,@binding,@release,@plan,@summary,@destructive)::text", ct,
            P("id", deploymentId), P("token", leaseToken), P("module", module.Id), P("version", module.Version), P("bindingsha", plan.BindingSha256),
            P("binding", JsonSerializer.Serialize(plan.Binding, Json), NpgsqlDbType.Jsonb), P("release", release.Sha256),
            P("plan", plan.PlanFile, NpgsqlDbType.Bytea), P("summary", JsonSerializer.Serialize(plan.Summary, Json), NpgsqlDbType.Jsonb),
            P("destructive", plan.Summary.Destructive));
    }

    public Task CompleteDeploymentAsync(Guid deploymentId, byte[] leaseToken, bool healthy, string url, DeploymentOutcome outcome, string? failureCode,
        CancellationToken ct = default)
        => Call("SELECT orchestration.complete_deployment(@id,@token,@healthy,@url,@result,@failure)::text", ct, P("id", deploymentId),
            P("token", leaseToken), P("healthy", healthy), P("url", url), P("result", JsonSerializer.Serialize(outcome, Json), NpgsqlDbType.Jsonb),
            P("failure", failureCode, NpgsqlDbType.Text));

    public Task FailDeploymentAsync(Guid deploymentId, byte[] leaseToken, string failureCode, DeploymentOutcome? outcome, CancellationToken ct = default)
        => Call("SELECT orchestration.fail_deployment(@id,@token,@failure,@result)::text", ct, P("id", deploymentId), P("token", leaseToken),
            P("failure", failureCode), P("result", outcome is null ? null : JsonSerializer.Serialize(outcome, Json), NpgsqlDbType.Jsonb));

    private sealed record RawDeploymentClaim(Guid DeploymentId, string ProjectId, string Phase, string TargetName, JsonElement Target,
        Guid DevelopmentJobId, string Evidence, string EvidenceSha256, string? BindingSha256, JsonElement? Binding, string? PlanFile,
        JsonElement? Summary, string? ModuleId, string? ModuleVersion, bool? AllowDestructive, string? ApprovedBy, DateTimeOffset? ApprovedAt);
}
