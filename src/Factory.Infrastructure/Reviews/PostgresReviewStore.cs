using Factory.Core.Reviews;
using Factory.Core.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Factory.Infrastructure.Reviews;

// Each operation owns its context/transaction; failed writes cannot poison a later call.
public sealed class PostgresReviewStore(DbContextOptions<ReviewDbContext> options) : IReviewStore
{
    public Task<Factory.Core.Lifecycle.GenerationOutcome> PublishManagedAsync(Factory.Core.Authorization.TrustedIdentity actor,
        Factory.Core.Lifecycle.GenerationCommand command, Factory.Core.Lifecycle.GenerationAdmission admission,
        byte[] executionToken, WorkflowResult result, CancellationToken cancellationToken = default)
    {
        using var db = new ReviewDbContext(options);
        return new Factory.Infrastructure.Lifecycle.PostgresLifecycleStore(db.Database.GetConnectionString()!)
            .PublishAsync(actor, command, admission, executionToken, result, cancellationToken);
    }

    // Retained signature. M6's database guard forbids creating new unlinked cases.
    public Task<ReviewCaseSnapshot> CreateAsync(WorkflowRequest request, WorkflowResult result, CancellationToken cancellationToken = default)
        => WriteAsync(Guid.NewGuid(), null, request, result, cancellationToken);

    public Task<ReviewCaseSnapshot> PublishAsync(Guid caseId, long expectedRevision, WorkflowRequest request,
        WorkflowResult result, CancellationToken cancellationToken = default)
        => WriteAsync(caseId, expectedRevision, request, result, cancellationToken);

    private async Task<ReviewCaseSnapshot> WriteAsync(Guid id, long? expectedRevision, WorkflowRequest request,
        WorkflowResult result, CancellationToken cancellationToken)
    {
        // Detach all caller-owned nested collections synchronously before the first await.
        var snapshot = ReviewArtifactSerializer.Read(ReviewArtifactSerializer.Create(id, 1, request, result, DateTimeOffset.UtcNow));
        await using var db = new ReviewDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var row = expectedRevision is null ? new ReviewCaseRow { Id = id, ProjectId = snapshot.Request.ProjectId }
            : await db.ReviewCases.SingleOrDefaultAsync(x => x.Id == id && x.ProjectId == snapshot.Request.ProjectId, cancellationToken)
                ?? throw new ReviewConflictException();
        if (expectedRevision is not null && row.Revision != expectedRevision) throw new ReviewConflictException();
        row.CurrentVersion = checked(row.CurrentVersion + 1);
        row.Revision = checked(row.Revision + 1);
        row.State = ReviewStateMachine.PublishRevision(row.State);
        if (expectedRevision is null) db.ReviewCases.Add(row);
        var detachedResult = new WorkflowResult(snapshot.WorkflowId, WorkflowStatus.AwaitingHumanReview,
            WorkflowStageStatus.Completed, WorkflowStageStatus.Completed, snapshot.Analysis, snapshot.Architecture);
        var artifact = ReviewArtifactSerializer.Create(id, row.CurrentVersion, snapshot.Request, detachedResult, snapshot.CreatedAt);
        try
        {
            // Claim the revision first. Concurrent publishers update the same concurrency token.
            await db.SaveChangesAsync(cancellationToken);
            db.ArtifactVersions.Add(new ArtifactVersionRow { CaseId = id, Version = row.CurrentVersion,
                WorkflowId = snapshot.WorkflowId, Content = artifact.GetContent(), ContentHash = artifact.ContentHash, CreatedAt = snapshot.CreatedAt });
            db.PublicationEvents.Add(new PublicationEventRow { Id = Guid.NewGuid(), CaseId = id,
                Version = row.CurrentVersion, ContentHash = artifact.ContentHash, CreatedAt = snapshot.CreatedAt });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Snapshot(row);
        }
        catch (DbUpdateConcurrencyException) { throw new ReviewConflictException(); }
    }

    public async Task<ReviewCaseSnapshot?> GetCaseAsync(string projectId, Guid caseId, CancellationToken cancellationToken = default)
    {
        await using var db = new ReviewDbContext(options);
        var row = await db.ReviewCases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == caseId && x.ProjectId == projectId, cancellationToken);
        return row is null ? null : Snapshot(row);
    }

    public async Task<ReviewArtifact?> GetArtifactAsync(string projectId, Guid caseId, int version, CancellationToken cancellationToken = default)
    {
        await using var db = new ReviewDbContext(options);
        var row = await (from artifact in db.ArtifactVersions.AsNoTracking()
                         join review in db.ReviewCases on artifact.CaseId equals review.Id
                         where review.ProjectId == projectId && artifact.CaseId == caseId && artifact.Version == version
                         select artifact).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        var result = new ReviewArtifact(caseId, version, row.Content, row.ContentHash);
        var envelope = ReviewArtifactSerializer.Read(result);
        if (envelope.Request.ProjectId != projectId || envelope.WorkflowId != row.WorkflowId || envelope.CreatedAt != row.CreatedAt)
            throw new ReviewIntegrityException();
        return result;
    }

    private static ReviewCaseSnapshot Snapshot(ReviewCaseRow row) => new(row.Id, row.ProjectId, row.CurrentVersion, row.Revision, row.State);
}
