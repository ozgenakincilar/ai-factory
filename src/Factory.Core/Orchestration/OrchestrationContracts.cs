using Factory.Core.Analysis;
using Factory.Core.Architecture;
using Factory.Core.Authorization;
using Factory.Core.Decomposition;
using Factory.Core.Workflows;

namespace Factory.Core.Orchestration;

public enum OrchestrationError { InvalidCommand, Forbidden, NotFound, Conflict, Capacity, LeaseLost, Integrity, OutcomeUnknown, TransactionAborted,
    BudgetExceeded, PriceUnknown, StaleRevision, CapabilityUnverified, Unavailable }

// The provider/model a worker is configured to call; required to price budget reservations.
public sealed record ModelIdentity(string Provider, string Model);

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
    // Upper bound per call (gpt-4o-mini allows 16,384); reservations price it as the worst case.
    public const int MaxOutputTokens = 16_000;
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

public enum ModelCallOutcome { Completed, Refused, Incomplete, Empty, Failed, Cancelled }

// One provider call as observed by the worker. Tokens are null when the provider reported none
// (failed or cancelled calls may still be billed); they are never reported as zero instead.
public sealed record ModelCall(string? Provider, string? Model, int? InputTokens, int? OutputTokens, int DurationMs, ModelCallOutcome Outcome,
    Guid? ReservationId = null);

public enum BudgetScope { Organization, Project, Provider }
public enum BudgetPeriod { Month, Lifetime }

// Spent is conservative: settled priced calls at actual estimate, every other reserved call at its
// reserved worst case. Remaining never goes below zero in the view.
public sealed record BudgetStatus(BudgetScope Scope, string ScopeId, BudgetPeriod Period, DateTimeOffset? PeriodStart,
    long LimitMicros, long SpentMicros, long RemainingMicros);

// Usage totals. EstimatedCostMicros (USD millionths) sums priced calls only; UnpricedCalls had
// tokens but no operator price; UnknownUsageCalls had no provider-reported tokens.
public sealed record UsageTotals(int Calls, long InputTokens, long OutputTokens, long EstimatedCostMicros, int UnpricedCalls, int UnknownUsageCalls);
public sealed record ModelUsageTotal(string? Provider, string? Model, UsageTotals Totals);
public sealed record ProjectUsage(string ProjectId, DateTimeOffset From, DateTimeOffset To, UsageTotals Totals, IReadOnlyList<ModelUsageTotal> Models);

public sealed record RunView(Guid RunId, string ProjectId, RunStatus Status, string? FailureCode,
    IReadOnlyList<WorkflowStep> Steps, RunPublication? Publication, IReadOnlyList<StepDecision>? Decisions = null, UsageTotals? Usage = null,
    IReadOnlyList<StepRoute>? Routes = null);

// One leased step. Prior checkpoints are revalidated by the store before they are returned.
// Decisions are this step's earlier human decisions, so a resumed step can act on them.
// NextVersion is the review artifact version publication will create (1 for a new work item).
// decomposition-v1 adds the approved plan, this step's part (Part steps) and the succeeded part
// proposals by part key (dependencies for a part, all parts for the merge step).
public sealed record ClaimedStep(Guid RunId, Guid AttemptId, int Ordinal, StepKind Kind, Guid CaseId,
    WorkflowRequest Request, IReadOnlyList<WorkflowStep> Steps, AnalysisResult? Analysis, ArchitectureProposal? Architecture,
    IReadOnlyList<StepDecision>? Decisions = null, int NextVersion = 1, string WorkflowVersion = WorkflowVersions.AnalysisArchitecture,
    IReadOnlyList<StepDependency>? Dependencies = null, DecompositionPlan? Plan = null, PlanPart? Part = null,
    IReadOnlyDictionary<string, ArchitectureProposal>? PartOutputs = null);

// The plan awaiting (or past) approval, with the tier each part would be routed to.
public sealed record PartRoute(string Key, ModelTier Tier, string Reason);
public sealed record RunPlan(Guid RunId, DecompositionPlan Plan, IReadOnlyList<PartRoute> Routes, string PolicyVersion);

// API-side port: admission, cancellation, human decisions and safe status reads, authorized per project.
public interface IWorkflowRunStore
{
    Task<RunSubmission> SubmitAsync(TrustedIdentity actor, Guid key, WorkflowRequest request, CancellationToken ct = default)
        => SubmitAsync(actor, key, request, WorkflowVersions.AnalysisArchitecture, ct);
    // workflowVersion is analysis-architecture-v1 or decomposition-v1 (the plan needs human approval).
    Task<RunSubmission> SubmitAsync(TrustedIdentity actor, Guid key, WorkflowRequest request, string workflowVersion, CancellationToken ct = default);
    // Same audience as GetRunAsync. NotFound until the plan exists (decomposition-v1 runs only).
    Task<RunPlan> GetPlanAsync(TrustedIdentity actor, string projectId, Guid runId, CancellationToken ct = default);
    // Regenerates an existing work item as the next version of its review case. expectedETag is the
    // work item's current strong ETag; a changed work item or case is StaleRevision (412).
    Task<RunSubmission> SubmitRegenerationAsync(TrustedIdentity actor, Guid key, Guid workItemId, string expectedETag, WorkflowRequest request,
        CancellationToken ct = default);
    Task<RunView> GetRunAsync(TrustedIdentity actor, string projectId, Guid runId, CancellationToken ct = default);
    // Generator; idempotent for an already cancelled run, Conflict for a finished run.
    Task<RunView> CancelAsync(TrustedIdentity actor, string projectId, Guid runId, CancellationToken ct = default);
    // Active Human Owner/Admin of the project's organization (M8.2); "stop" cancels the run.
    Task<RunView> DecideAsync(TrustedIdentity actor, string projectId, Guid runId, Guid decisionId, string choice, CancellationToken ct = default);
    // Same audience as GetRunAsync; throws Forbidden otherwise. Checked when a client starts watching.
    Task AuthorizeWatchAsync(TrustedIdentity actor, string projectId, CancellationToken ct = default);
    // Same audience as GetRunAsync; half-open interval [from, to), at most 366 days.
    Task<ProjectUsage> GetUsageAsync(TrustedIdentity actor, string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
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
    Task CompletePlanningAsync(Guid attemptId, byte[] leaseToken, DecompositionPlan plan, CancellationToken ct = default);
    Task CompletePartAsync(Guid attemptId, byte[] leaseToken, ArchitectureProposal proposal, CancellationToken ct = default);
    // Completes plan approval after a recorded "approve" decision and creates the part steps atomically.
    Task ApprovePlanAsync(Guid attemptId, byte[] leaseToken, CancellationToken ct = default);
    Task FailAsync(Guid attemptId, byte[] leaseToken, string failureCode, CancellationToken ct = default);
    // The customer model endpoint snapshotted on this attempt's run, or null for the Factory default.
    // Lease-authorized; the credential stays encrypted.
    Task<Factory.Core.Tenancy.ModelEndpoint?> GetModelEndpointAsync(Guid attemptId, byte[] leaseToken, CancellationToken ct = default);
    // Records the routing evidence for this attempt once, before its first provider call.
    // Lease-authorized; a second route for the same attempt is a Conflict.
    Task RecordRouteAsync(Guid attemptId, byte[] leaseToken, ModelRoute route, ModelIdentity? model, CancellationToken ct = default);
    // Reserves the worst-case cost of one call against every applicable budget before it is made.
    // Throws BudgetExceeded or PriceUnknown (a budget applies but the model has no price).
    Task<Guid> ReserveCostAsync(Guid attemptId, byte[] leaseToken, ModelIdentity model, int estimatedInputTokens, int maxOutputTokens,
        CancellationToken ct = default);
    // Appends usage for one provider call of this attempt. Accepted even after the lease ended,
    // because a call that returned late was still made; the token proves the caller owned it.
    Task RecordModelCallAsync(Guid attemptId, byte[] leaseToken, ModelCall call, CancellationToken ct = default);
    // Ends the attempt and parks the step as Waiting until a human decides; releases worker capacity.
    Task<Guid> RequestDecisionAsync(Guid attemptId, byte[] leaseToken, string reason, IReadOnlyList<string> options, CancellationToken ct = default);
    // Atomically publishes the managed review case and completes the run.
    Task<RunPublication> PublishAsync(ClaimedStep step, byte[] leaseToken, CancellationToken ct = default);
}
