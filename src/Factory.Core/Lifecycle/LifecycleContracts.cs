using System.Globalization;
using Factory.Core.Authorization;
using Factory.Core.Reviews;
using Factory.Core.Workflows;

namespace Factory.Core.Lifecycle;

public enum GenerationStatus { Running, Published, GenerationFailed, Cancelled, PublicationFailed, Abandoned }
public enum LifecycleError { InvalidCommand, Forbidden, NotFound, StaleRevision, Conflict, Capacity, Expired, Integrity, OutcomeUnknown, TransactionAborted }
public sealed class LifecycleException(LifecycleError error) : Exception("lifecycle_operation_failed")
{
    public LifecycleError Error { get; } = error;
    public Guid? RunId { get; init; }
    public Guid? WorkItemId { get; init; }
}

public static class LifecycleLimits
{
    public const int ExecutionSeconds = 180;
    public const int ConcurrentAttempts = 4;
    public const int ProviderCalls = 2;
    public const int RequestBytes = 65536;
    public const string WorkflowVersion = "analysis-architecture-v1";
}

public sealed record GenerationCommand(Guid Key, Guid? WorkItemId, string? ExpectedETag,
    string WorkflowVersion, WorkflowRequest Request);
public sealed record PublicationReference(Guid CaseId, int Version, long Revision, string ContentHash);
// Safe historical projection: never contains prompts, artifact bytes, actor details or reasons.
public sealed record GenerationOutcome(Guid WorkItemId, Guid RunId, GenerationStatus Status,
    int HttpStatus, string? FailureCode, PublicationReference? Publication);
public sealed record GenerationAdmission(GenerationOutcome Outcome, bool Replayed,
    Guid CaseId, int NextVersion, long ExpectedRevision, DateTimeOffset Deadline);
public sealed record GenerationResponse(GenerationOutcome Outcome, bool Replayed);
public sealed record WorkItemSnapshot(Guid WorkItemId, string ProjectId, long Revision,
    Guid? ActiveRunId, Guid LatestRunId, GenerationStatus LatestStatus,
    Guid? CaseId, long CaseRevision, int? ArtifactVersion, string? ContentHash, ReviewState? ReviewState)
{
    public string ETag => WorkItemETag.Format(WorkItemId, Revision, CaseId, CaseRevision);
}

public static class WorkItemETag
{
    public static string Format(Guid workItem, long revision, Guid? review, long reviewRevision)
        => string.Create(CultureInfo.InvariantCulture, $"\"wi-v1:{workItem:N}:{revision}:{(review is null ? "none" : review.Value.ToString("N"))}:{reviewRevision}\"");

    public static bool IsValid(string? tag, Guid workItem)
    {
        if (tag is null || !tag.StartsWith('"') || !tag.EndsWith('"')) return false;
        var parts = tag[1..^1].Split(':');
        if (parts.Length != 5 || parts[0] != "wi-v1" || parts[1] != workItem.ToString("N") ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 1 ||
            !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var caseRevision)) return false;
        Guid? id = null;
        if (parts[3] != "none")
        {
            if (!Guid.TryParseExact(parts[3], "N", out var parsed) || parsed == Guid.Empty || caseRevision < 1) return false;
            id = parsed;
        }
        else if (caseRevision != 0) return false;
        return tag == Format(workItem, revision, id, caseRevision);
    }
}

public interface ILifecycleStore
{
    // executionToken is private application ownership, never a request field or replay result.
    Task<GenerationAdmission> AdmitAsync(TrustedIdentity actor, GenerationCommand command, byte[] executionToken, CancellationToken ct = default);
    Task<GenerationOutcome> PublishAsync(TrustedIdentity actor, GenerationCommand command, GenerationAdmission admission,
        byte[] executionToken, WorkflowResult result, CancellationToken ct = default);
    Task<GenerationOutcome> FinishAsync(TrustedIdentity actor, string projectId, Guid runId, byte[] executionToken,
        GenerationStatus status, string failureCode, CancellationToken ct = default);
    Task<WorkItemSnapshot> GetWorkItemAsync(TrustedIdentity actor, string projectId, Guid workItemId, CancellationToken ct = default);
    Task<GenerationOutcome> GetRunAsync(TrustedIdentity actor, string projectId, Guid runId, CancellationToken ct = default);
}
