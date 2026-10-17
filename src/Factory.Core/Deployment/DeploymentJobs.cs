using System.Text.Json;
using Factory.Core.Authorization;

namespace Factory.Core.Deployment;

// M13A.2 durable deployments: a Generator requests the deployment of a verified development job to a
// target of the same project; the worker plans; an Owner/Admin of the project's organization approves
// or rejects that exact plan (its binding hash); the worker applies it and verifies the release.

public enum DeploymentStatus { Queued, Planning, AwaitingApproval, Approved, Applying, Succeeded, Unhealthy, Failed, Rejected, Expired }

public sealed record DeploymentTargetSummary(string Name, string Cloud, string Environment, string Application, string? Location, string? ResourceGroup);

public sealed record DeploymentSubmission(Guid DeploymentId, DeploymentStatus Status, bool Replayed);

public sealed record DeploymentSummaryView(Guid DeploymentId, string TargetName, Guid DevelopmentJobId, DeploymentStatus Status, string? FailureCode,
    string? BindingSha256, string? ReleaseSha256, string? ModuleId, string? ModuleVersion, PlanSummary? Summary, bool? Destructive,
    DateTimeOffset? PlannedAt, bool? Approved, DateTimeOffset? DecidedAt, bool? AllowDestructive, string? Url, DeploymentOutcome? Result,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DeploymentBinding? Binding = null, string? PlanSha256 = null);

public sealed record DeploymentList(IReadOnlyList<DeploymentTargetSummary> Targets, IReadOnlyList<DeploymentSummaryView> Deployments);

// What the worker records after apply: the approved binding, the apply outputs, the health check and
// the configuration check (secret references resolved). Never credentials or tool output with them.
public sealed record DeploymentOutcome(string BindingSha256, string? Url, IReadOnlyDictionary<string, string>? Outputs, HealthResult? Health,
    ConfigurationCheck? Configuration, string? Failure, string? ApprovedBy, DateTimeOffset? ApprovedAt);

public sealed record ConfigurationCheck(bool Resolved, IReadOnlyList<ConfigurationReference> References);

public sealed record ConfigurationReference(string Name, string Status);

public interface IConfigurationChecker
{
    // Checks that the deployed application's secret references resolve (for example Key Vault references).
    Task<ConfigurationCheck> CheckAsync(DeploymentTarget target, IReadOnlyDictionary<string, string> outputs, CancellationToken ct = default);
}

public interface IDeploymentJobs
{
    Task<DeploymentSubmission> SubmitDeploymentAsync(TrustedIdentity actor, string projectId, Guid key, string targetName, Guid developmentJobId,
        CancellationToken ct = default);
    Task<DeploymentList> ListDeploymentsAsync(TrustedIdentity actor, string projectId, CancellationToken ct = default);
    Task<DeploymentSummaryView> GetDeploymentAsync(TrustedIdentity actor, string projectId, Guid deploymentId, CancellationToken ct = default);
    // Approves or rejects exactly the plan with this binding hash; a stale or other plan is refused.
    Task<DeploymentSummaryView> DecideDeploymentAsync(TrustedIdentity actor, string projectId, Guid deploymentId, bool approve, string bindingSha256,
        bool allowDestructive, CancellationToken ct = default);
}

public enum DeploymentPhase { Plan, Apply }

public sealed record ClaimedDeployment(Guid DeploymentId, string ProjectId, DeploymentPhase Phase, string TargetName, DeploymentTarget Target,
    Guid DevelopmentJobId, byte[] Evidence, string EvidenceSha256, string? BindingSha256, DeploymentBinding? Binding, byte[]? PlanFile,
    PlanSummary? Summary, string? ModuleId, string? ModuleVersion, bool? AllowDestructive, string? ApprovedBy, DateTimeOffset? ApprovedAt);

public interface IDeploymentQueue
{
    Task<ClaimedDeployment?> ClaimDeploymentAsync(string workerId, byte[] leaseToken, CancellationToken ct = default);
    Task RenewDeploymentAsync(Guid deploymentId, byte[] leaseToken, CancellationToken ct = default);
    Task RecordPlanAsync(Guid deploymentId, byte[] leaseToken, DeploymentModule module, ReleasePackage release, DeploymentPlan plan, CancellationToken ct = default);
    Task CompleteDeploymentAsync(Guid deploymentId, byte[] leaseToken, bool healthy, string url, DeploymentOutcome outcome, string? failureCode,
        CancellationToken ct = default);
    Task FailDeploymentAsync(Guid deploymentId, byte[] leaseToken, string failureCode, DeploymentOutcome? outcome, CancellationToken ct = default);
}

public static class DeploymentJobLimits
{
    public const int LeaseSeconds = 90;
    public const int RenewSeconds = 30;
    public const int MaxPlanBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan ApprovalValidity = TimeSpan.FromHours(24);
}

public static class DeploymentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false) },
    };
}
