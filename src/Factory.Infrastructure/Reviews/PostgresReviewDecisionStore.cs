using Factory.Core.Authorization;
using Factory.Core.Reviews;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Factory.Infrastructure.Reviews;

public sealed class PostgresReviewDecisionStore(DbContextOptions<ReviewDbContext> options) : IReviewDecisionStore
{
    private static void CheckIdentity(TrustedIdentity identity)
    {
        if (identity.Kind != PrincipalKind.Human) throw new ReviewCommandException(ReviewError.Forbidden);
    }

    private static async Task<Guid> Authorize(ReviewDbContext db, TrustedIdentity identity, string project, CancellationToken ct)
    {
        CheckIdentity(identity);
        try
        {
            return await db.Database.SqlQuery<Guid>($"""
                SELECT "PrincipalId" AS "Value" FROM review.lock_reviewer({project}, {identity.Issuer}, {identity.Subject})
                """).SingleAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "42501" && ex.MessageText == "review_forbidden")
        { throw new ReviewCommandException(ReviewError.Forbidden); }
    }

    public async Task<ReviewSnapshot> GetAsync(TrustedIdentity identity, string projectId, Guid caseId, CancellationToken cancellationToken = default)
    {
        await using var db = new ReviewDbContext(options);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await Authorize(db, identity, projectId, cancellationToken);
        // One statement: no torn version/revision/hash/decision combination across publication.
        var row = await (from c in db.ReviewCases.AsNoTracking()
                         join a in db.ArtifactVersions on new { CaseId = c.Id, Version = c.CurrentVersion } equals new { a.CaseId, a.Version }
                         join d in db.Set<DecisionEventRow>() on new { CaseId = c.Id, Version = c.CurrentVersion } equals new { d.CaseId, d.Version } into decisions
                         from d in decisions.DefaultIfEmpty()
                         where c.Id == caseId && c.ProjectId == projectId
                         select new { Case = c, Hash = a.ContentHash, Decision = d }).SingleOrDefaultAsync(cancellationToken)
            ?? throw new ReviewCommandException(ReviewError.NotFound);
        await tx.CommitAsync(cancellationToken);
        return new(row.Case.Id, row.Case.ProjectId, row.Case.CurrentVersion, row.Hash, row.Case.Revision, row.Case.State,
            row.Decision is null ? null : Summary(row.Decision));
    }

    public async Task<ReviewArtifact> GetArtifactAsync(TrustedIdentity identity, string projectId, Guid caseId, int version, CancellationToken cancellationToken = default)
    {
        await using var db = new ReviewDbContext(options);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await Authorize(db, identity, projectId, cancellationToken);
        var artifact = await ReadArtifact(db, projectId, caseId, version, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return artifact;
    }

    private static async Task<ReviewArtifact> ReadArtifact(ReviewDbContext db, string project, Guid id, int version, CancellationToken ct)
    {
        var row = await (from a in db.ArtifactVersions.AsNoTracking() join c in db.ReviewCases on a.CaseId equals c.Id
                         where c.ProjectId == project && c.Id == id && a.Version == version select a).SingleOrDefaultAsync(ct)
            ?? throw new ReviewCommandException(ReviewError.NotFound);
        var artifact = new ReviewArtifact(id, version, row.Content, row.ContentHash);
        var envelope = ReviewArtifactSerializer.Read(artifact);
        if (envelope.Request.ProjectId != project || envelope.WorkflowId != row.WorkflowId || envelope.CreatedAt != row.CreatedAt)
            throw new ReviewIntegrityException();
        return artifact;
    }

    public async Task<DecisionOutcome> DecideAsync(TrustedIdentity identity, string projectId, Guid caseId, DecisionCommand command, CancellationToken cancellationToken = default)
    {
        CheckIdentity(identity);
        DecisionValidation.Validate(projectId, caseId, command);
        try { return await Execute(identity, projectId, caseId, command, false, cancellationToken); }
        catch (ReviewCommandException ex) when (ex.Error == ReviewError.StaleRevision)
        { return await Execute(identity, projectId, caseId, command, true, cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            // Reconcile a concurrent identical command, never retry a write automatically.
            return await Execute(identity, projectId, caseId, command, true, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" } p &&
            p.ConstraintName is "PK_CommandReceipts" or "IX_DecisionEvents_CaseId_Version")
        { return await Execute(identity, projectId, caseId, command, true, cancellationToken); }
    }

    private async Task<DecisionOutcome> Execute(TrustedIdentity identity, string project, Guid id, DecisionCommand command, bool reconcileOnly, CancellationToken ct)
    {
        await using var db = new ReviewDbContext(options);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actor = await Authorize(db, identity, project, ct);
        var action = command.Decision.ToString();
        var fingerprint = await db.Database.SqlQuery<string>($"""
            SELECT review.decision_fingerprint({project}, {actor}, {id}, {command.ExpectedRevision},
                {command.ArtifactVersion}, {command.ArtifactSha256}, {action}, {command.Reason}::text) AS "Value"
            """).SingleAsync(ct);
        var receipt = await db.Set<CommandReceiptRow>().AsNoTracking().SingleOrDefaultAsync(x => x.ProjectId == project &&
            x.PrincipalId == actor && x.CommandVersion == 1 && x.Key == command.IdempotencyKey, ct);
        if (receipt is not null)
        {
            if (receipt.Fingerprint != fingerprint) throw new ReviewCommandException(ReviewError.IdempotencyConflict);
            var original = await db.Set<DecisionEventRow>().AsNoTracking().SingleAsync(x => x.Id == receipt.DecisionId, ct);
            await tx.CommitAsync(ct);
            return new(Snapshot(original), true);
        }
        if (reconcileOnly) throw new ReviewCommandException(ReviewError.StaleRevision);
        var review = await db.ReviewCases.SingleOrDefaultAsync(x => x.Id == id && x.ProjectId == project, ct)
            ?? throw new ReviewCommandException(ReviewError.NotFound);
        if (review.Revision != command.ExpectedRevision) throw new ReviewCommandException(ReviewError.StaleRevision);
        if (review.CurrentVersion != command.ArtifactVersion) throw new ReviewCommandException(ReviewError.ArtifactMismatch);
        if (review.State != ReviewState.PendingReview) throw new ReviewCommandException(ReviewError.AlreadyDecided);
        var artifact = await ReadArtifact(db, project, id, command.ArtifactVersion, ct);
        if (artifact.ContentHash != command.ArtifactSha256) throw new ReviewCommandException(ReviewError.ArtifactMismatch);
        var aggregate = ReviewCase.Restore(new(review.Id, review.ProjectId, review.CurrentVersion, review.Revision, review.State), artifact.ContentHash);
        aggregate.Decide(command.ExpectedRevision, command.ArtifactVersion, command.ArtifactSha256, command.Decision, command.Reason);
        review.State = aggregate.State;
        review.Revision = aggregate.Revision;
        await db.SaveChangesAsync(ct); // Same concurrency token used by PublishAsync.
        var decision = new DecisionEventRow
        {
            Id = Guid.NewGuid(), ProjectId = project, CaseId = id, Version = command.ArtifactVersion,
            ContentHash = artifact.ContentHash, PrincipalId = actor, Issuer = identity.Issuer, Subject = identity.Subject,
            ExpectedRevision = command.ExpectedRevision, ResultRevision = review.Revision, Decision = command.Decision,
            Reason = command.Reason, ResultState = review.State, Key = command.IdempotencyKey, Fingerprint = fingerprint
        };
        db.Add(decision);
        await db.SaveChangesAsync(ct); // Trigger reauthorizes and supplies timestamp, fingerprint and auth revisions.
        db.Add(new CommandReceiptRow { ProjectId = project, PrincipalId = actor, Key = command.IdempotencyKey,
            DecisionId = decision.Id, Fingerprint = decision.Fingerprint });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct); // Deferred integrity checks execute here, before returning success.
        return new(Snapshot(decision), false);
    }

    private static DecisionSummary Summary(DecisionEventRow d) => new(d.Id, d.Decision, d.Reason, d.PrincipalId, d.CreatedAt);
    private static ReviewSnapshot Snapshot(DecisionEventRow d) => new(d.CaseId, d.ProjectId, d.Version, d.ContentHash,
        d.ResultRevision, d.ResultState, Summary(d));
}
