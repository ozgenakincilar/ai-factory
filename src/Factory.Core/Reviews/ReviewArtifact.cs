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

// M6 exposes only PublishManagedAsync to its authenticated lifecycle application service.
// Legacy signatures remain for historical cases; the migrated database denies unlinked CreateAsync.
// Human decisions retain their separate IReviewDecisionStore boundary.
public interface IReviewStore
{
    Task<Factory.Core.Lifecycle.GenerationOutcome> PublishManagedAsync(Factory.Core.Authorization.TrustedIdentity actor,
        Factory.Core.Lifecycle.GenerationCommand command, Factory.Core.Lifecycle.GenerationAdmission admission,
        byte[] executionToken, WorkflowResult result, CancellationToken cancellationToken = default);
    Task<ReviewCaseSnapshot> CreateAsync(WorkflowRequest request, WorkflowResult result, CancellationToken cancellationToken = default);
    Task<ReviewCaseSnapshot> PublishAsync(Guid caseId, long expectedRevision, WorkflowRequest request,
        WorkflowResult result, CancellationToken cancellationToken = default);
    Task<ReviewCaseSnapshot?> GetCaseAsync(string projectId, Guid caseId, CancellationToken cancellationToken = default);
    Task<ReviewArtifact?> GetArtifactAsync(string projectId, Guid caseId, int version, CancellationToken cancellationToken = default);
}
