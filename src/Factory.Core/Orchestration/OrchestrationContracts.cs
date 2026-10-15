using Factory.Core.Analysis;
using Factory.Core.Architecture;
using Factory.Core.Authorization;
using Factory.Core.Workflows;

namespace Factory.Core.Orchestration;

public enum OrchestrationError { InvalidCommand, Forbidden, NotFound, Conflict, Capacity, LeaseLost, Integrity, OutcomeUnknown, TransactionAborted }

public sealed class OrchestrationException(OrchestrationError error) : Exception("orchestration_operation_failed")
{
    public OrchestrationError Error { get; } = error;
}

public static class DurableWorkflowLimits
{
    public const string WorkflowVersion = "analysis-architecture-v1";
    // Lease and heartbeat are fixed by the database; workers cannot choose longer leases.
    public const int LeaseSeconds = 60;
    public const int HeartbeatSeconds = 20;
    public const int StepSeconds = 150;
    public const int MaxModelStepAttempts = 2;
    public const int MaxPublicationAttempts = 3;
    public const int ActiveRunsPerProject = 20;
}

// Safe projections: never prompts, model outputs, artifact bytes, lease tokens or reasons.
public sealed record RunSubmission(Guid RunId, RunStatus Status, bool Replayed);
public sealed record RunPublication(Guid WorkItemId, Guid CaseId, int Version, string ContentHash);
public sealed record RunView(Guid RunId, string ProjectId, RunStatus Status, string? FailureCode,
    IReadOnlyList<WorkflowStep> Steps, RunPublication? Publication);

// One leased step. Prior checkpoints are revalidated by the store before they are returned.
public sealed record ClaimedStep(Guid RunId, Guid AttemptId, int Ordinal, StepKind Kind, Guid CaseId,
    WorkflowRequest Request, IReadOnlyList<WorkflowStep> Steps, AnalysisResult? Analysis, ArchitectureProposal? Architecture);

// API-side port: admission and safe status reads, authorized per project.
public interface IWorkflowRunStore
{
    Task<RunSubmission> SubmitAsync(TrustedIdentity actor, Guid key, WorkflowRequest request, CancellationToken ct = default);
    Task<RunView> GetRunAsync(TrustedIdentity actor, string projectId, Guid runId, CancellationToken ct = default);
}

// Worker-side port. leaseToken is private worker ownership; only its digest is stored.
public interface IStepQueue
{
    Task<ClaimedStep?> ClaimAsync(string workerId, byte[] leaseToken, CancellationToken ct = default);
    Task RenewAsync(Guid attemptId, byte[] leaseToken, CancellationToken ct = default);
    Task CompleteAnalysisAsync(Guid attemptId, byte[] leaseToken, AnalysisResult analysis, CancellationToken ct = default);
    Task CompleteArchitectureAsync(Guid attemptId, byte[] leaseToken, ArchitectureProposal architecture, CancellationToken ct = default);
    Task FailAsync(Guid attemptId, byte[] leaseToken, string failureCode, CancellationToken ct = default);
    // Atomically publishes the managed review case and completes the run.
    Task<RunPublication> PublishAsync(ClaimedStep step, byte[] leaseToken, CancellationToken ct = default);
}
