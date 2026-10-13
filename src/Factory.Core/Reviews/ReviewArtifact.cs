using Factory.Core.Analysis;
using Factory.Core.Architecture;
using Factory.Core.Workflows;

namespace Factory.Core.Reviews;

public sealed record ReviewEnvelope(int FormatVersion, string ValidatorVersion,
    Guid CaseId, int Version, Guid WorkflowId, DateTimeOffset CreatedAt,
    WorkflowRequest Request, AnalysisResult Analysis, ArchitectureProposal Architecture);

public sealed record ReviewCaseSnapshot(Guid Id, string ProjectId, int CurrentVersion,
    long Revision, ReviewState State);

// Defensive copies prevent a caller from changing the bytes after verification.
public sealed class ReviewArtifact(Guid caseId, int version, byte[] content, string contentHash)
{
    private readonly byte[] _content = content.ToArray();
    public Guid CaseId { get; } = caseId;
    public int Version { get; } = version;
    public string ContentHash { get; } = contentHash;
    public byte[] GetContent() => _content.ToArray();
}

public sealed class ReviewConflictException() : Exception("review_version_conflict");
public sealed class ReviewIntegrityException() : Exception("review_artifact_integrity_failure");

// Internal application boundary, deliberately not registered in the HTTP API in 5A.
// Authentication, decisions, membership, and command idempotency belong to 5B.
public interface IReviewStore
{
    Task<ReviewCaseSnapshot> CreateAsync(WorkflowRequest request, WorkflowResult result, CancellationToken cancellationToken = default);
    Task<ReviewCaseSnapshot> PublishAsync(Guid caseId, long expectedRevision, WorkflowRequest request,
        WorkflowResult result, CancellationToken cancellationToken = default);
    Task<ReviewCaseSnapshot?> GetCaseAsync(string projectId, Guid caseId, CancellationToken cancellationToken = default);
    Task<ReviewArtifact?> GetArtifactAsync(string projectId, Guid caseId, int version, CancellationToken cancellationToken = default);
}
