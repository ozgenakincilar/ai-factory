namespace Factory.Core.Reviews;

// Behavior and identity only. Authorization, immutable byte validation and transactions remain
// separate boundaries; PostgreSQL independently enforces persisted transitions.
public sealed class ReviewCase
{
    public Guid Id { get; }
    public string ProjectId { get; }
    public int Version { get; private set; }
    public long Revision { get; private set; }
    public ReviewState State { get; private set; }
    public string ContentHash { get; private set; }

    private ReviewCase(Guid id, string project, int version, long revision, ReviewState state, string hash)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(project) || version < 1 || revision < version ||
            !Enum.IsDefined(state) || !ValidHash(hash)) throw new ArgumentException("invalid_review_case");
        Id = id; ProjectId = project; Version = version; Revision = revision; State = state; ContentHash = hash;
    }

    public static ReviewCase Create(Guid id, string project, string hash) => new(id, project, 1, 1, ReviewState.PendingReview, hash);
    public static ReviewCase Restore(ReviewCaseSnapshot snapshot, string hash)
        => new(snapshot.Id, snapshot.ProjectId, snapshot.CurrentVersion, snapshot.Revision, snapshot.State, hash);

    public void Publish(long expectedRevision, string hash)
    {
        if (Revision != expectedRevision) throw new ReviewConflictException();
        if (!ValidHash(hash)) throw new ArgumentException("invalid_artifact_hash");
        var version = checked(Version + 1); var revision = checked(Revision + 1);
        State = ReviewStateMachine.PublishRevision(State);
        Version = version; Revision = revision; ContentHash = hash;
    }

    public void Decide(long expectedRevision, int version, string hash, ReviewDecision decision, string? reason)
    {
        if (Revision != expectedRevision) throw new ReviewConflictException();
        if (Version != version || ContentHash != hash) throw new ReviewIntegrityException();
        var revision = checked(Revision + 1);
        State = ReviewStateMachine.Decide(State, decision, reason); Revision = revision;
    }

    public ReviewCaseSnapshot Snapshot() => new(Id, ProjectId, Version, Revision, State);
    private static bool ValidHash(string hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
