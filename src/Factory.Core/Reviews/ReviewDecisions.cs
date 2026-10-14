using Factory.Core.Authorization;

namespace Factory.Core.Reviews;

public sealed record DecisionCommand(long ExpectedRevision, int ArtifactVersion, string ArtifactSha256,
    ReviewDecision Decision, string? Reason, Guid IdempotencyKey);
public sealed record DecisionSummary(Guid Id, ReviewDecision Decision, string? Reason, Guid PrincipalId, DateTimeOffset CreatedAt);
public sealed record ReviewSnapshot(Guid Id, string ProjectId, int CurrentVersion, string ContentHash,
    long Revision, ReviewState State, DecisionSummary? Decision);
public sealed record DecisionOutcome(ReviewSnapshot Snapshot, bool Replayed);
public enum ReviewError { InvalidCommand, Forbidden, NotFound, StaleRevision, ArtifactMismatch, AlreadyDecided, IdempotencyConflict }
public sealed class ReviewCommandException(ReviewError error) : Exception("review_command_rejected")
{
    public ReviewError Error { get; } = error;
}

public interface IReviewDecisionStore
{
    Task<ReviewSnapshot> GetAsync(TrustedIdentity identity, string projectId, Guid caseId, CancellationToken cancellationToken = default);
    Task<ReviewArtifact> GetArtifactAsync(TrustedIdentity identity, string projectId, Guid caseId, int version, CancellationToken cancellationToken = default);
    Task<DecisionOutcome> DecideAsync(TrustedIdentity identity, string projectId, Guid caseId, DecisionCommand command, CancellationToken cancellationToken = default);
}

public static class DecisionValidation
{
    public static void Validate(string projectId, Guid caseId, DecisionCommand command)
    {
        if (string.IsNullOrWhiteSpace(projectId) || projectId.Length > 200 || caseId == Guid.Empty ||
            command.ExpectedRevision < 1 || command.ExpectedRevision == long.MaxValue || command.ArtifactVersion < 1 ||
            command.IdempotencyKey == Guid.Empty || !Enum.IsDefined(command.Decision) ||
            command.ArtifactSha256 is not { Length: 64 } || command.ArtifactSha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            !DecisionReasonPolicy.IsValid(command.Decision, command.Reason))
            throw new ReviewCommandException(ReviewError.InvalidCommand);
    }
}
