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

public enum DecisionStatus { Pending, Decided, Cancelled }

// Decision reasons and options are static codes chosen by Factory code, never model or user text.
// Every decision offers "stop"; there is no timeout and no automatic default.
public static class DecisionCodes
{
    public const string Stop = "stop";
    public static bool IsCode(string? value) => value is { Length: > 0 and <= 64 } && value.All(c => c is >= 'a' and <= 'z' or '_');
    public static bool AreValidOptions(IReadOnlyList<string>? options) => options is { Count: >= 2 and <= 8 } &&
        options.All(IsCode) && options.Distinct(StringComparer.Ordinal).Count() == options.Count && options.Contains(Stop);
}

// Safe projections: never prompts, model outputs, artifact bytes, lease tokens or reasons.
public sealed record RunSubmission(Guid RunId, RunStatus Status, bool Replayed);
public sealed record RunPublication(Guid WorkItemId, Guid CaseId, int Version, string ContentHash);
public sealed record StepDecision(Guid Id, int Ordinal, string Reason, IReadOnlyList<string> Options, DecisionStatus Status, string? Choice);
public sealed record RunView(Guid RunId, string ProjectId, RunStatus Status, string? FailureCode,
    IReadOnlyList<WorkflowStep> Steps, RunPublication? Publication, IReadOnlyList<StepDecision>? Decisions = null);

// One leased step. Prior checkpoints are revalidated by the store before they are returned.
// Decisions are this step's earlier human decisions, so a resumed step can act on them.
public sealed record ClaimedStep(Guid RunId, Guid AttemptId, int Ordinal, StepKind Kind, Guid CaseId,
    WorkflowRequest Request, IReadOnlyList<WorkflowStep> Steps, AnalysisResult? Analysis, ArchitectureProposal? Architecture,
    IReadOnlyList<StepDecision>? Decisions = null);

// API-side port: admission, cancellation, human decisions and safe status reads, authorized per project.
public interface IWorkflowRunStore
{
    Task<RunSubmission> SubmitAsync(TrustedIdentity actor, Guid key, WorkflowRequest request, CancellationToken ct = default);
    Task<RunView> GetRunAsync(TrustedIdentity actor, string projectId, Guid runId, CancellationToken ct = default);
    // Generator; idempotent for an already cancelled run, Conflict for a finished run.
    Task<RunView> CancelAsync(TrustedIdentity actor, string projectId, Guid runId, CancellationToken ct = default);
    // Human project Reviewer (interim until M8 tenant roles); "stop" cancels the run.
    Task<RunView> DecideAsync(TrustedIdentity actor, string projectId, Guid runId, Guid decisionId, string choice, CancellationToken ct = default);
    // Same audience as GetRunAsync; throws Forbidden otherwise. Checked when a client starts watching.
    Task AuthorizeWatchAsync(TrustedIdentity actor, string projectId, CancellationToken ct = default);
}

// A run changed and was committed. A hint only: clients re-read authoritative state.
public sealed record RunChanged(string ProjectId, Guid RunId, RunStatus Status);

// Worker-side port. leaseToken is private worker ownership; only its digest is stored.
public interface IStepQueue
{
    Task<ClaimedStep?> ClaimAsync(string workerId, byte[] leaseToken, CancellationToken ct = default);
    Task RenewAsync(Guid attemptId, byte[] leaseToken, CancellationToken ct = default);
    Task CompleteAnalysisAsync(Guid attemptId, byte[] leaseToken, AnalysisResult analysis, CancellationToken ct = default);
    Task CompleteArchitectureAsync(Guid attemptId, byte[] leaseToken, ArchitectureProposal architecture, CancellationToken ct = default);
    Task FailAsync(Guid attemptId, byte[] leaseToken, string failureCode, CancellationToken ct = default);
    // Ends the attempt and parks the step as Waiting until a human decides; releases worker capacity.
    Task<Guid> RequestDecisionAsync(Guid attemptId, byte[] leaseToken, string reason, IReadOnlyList<string> options, CancellationToken ct = default);
    // Atomically publishes the managed review case and completes the run.
    Task<RunPublication> PublishAsync(ClaimedStep step, byte[] leaseToken, CancellationToken ct = default);
}
