using Factory.Core.Reviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Factory.Infrastructure.Reviews;

public sealed class ReviewCaseRow
{
    public Guid Id { get; set; }
    public string ProjectId { get; set; } = "";
    public int CurrentVersion { get; set; }
    public long Revision { get; set; }
    public ReviewState State { get; set; }
}

public sealed class ArtifactVersionRow
{
    public Guid CaseId { get; set; }
    public int Version { get; set; }
    public Guid WorkflowId { get; set; }
    public byte[] Content { get; set; } = [];
    public string ContentHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

// System publication audit only. No reviewer identity or approval can be persisted in 5A.
public sealed class PublicationEventRow
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public int Version { get; set; }
    public string ContentHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ReviewDbContext(DbContextOptions<ReviewDbContext> options) : DbContext(options)
{
    public DbSet<ReviewCaseRow> ReviewCases => Set<ReviewCaseRow>();
    public DbSet<ArtifactVersionRow> ArtifactVersions => Set<ArtifactVersionRow>();
    public DbSet<PublicationEventRow> PublicationEvents => Set<PublicationEventRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("review");
        Factory.Infrastructure.Authorization.AccessModel.Configure(model);
        model.Entity<ReviewCaseRow>(b =>
        {
            b.ToTable("ReviewCases", t =>
            {
                t.HasCheckConstraint("case_version_positive", "\"CurrentVersion\" > 0 AND \"Revision\" > 0");
                t.HasCheckConstraint("case_project_required", "length(btrim(\"ProjectId\")) > 0");
                t.HasCheckConstraint("case_pending_only_5a", "\"State\" = 'PendingReview'");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.ProjectId).HasMaxLength(200).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.Revision).IsConcurrencyToken();
        });
        model.Entity<ArtifactVersionRow>(b =>
        {
            b.ToTable("ArtifactVersions", t =>
            {
                t.HasCheckConstraint("artifact_version_positive", "\"Version\" > 0");
                t.HasCheckConstraint("artifact_hash_matches", "\"ContentHash\" = encode(sha256(\"Content\"), 'hex')");
            });
            b.HasKey(x => new { x.CaseId, x.Version });
            b.HasAlternateKey(x => new { x.CaseId, x.Version, x.ContentHash });
            b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.Content).IsRequired();
            b.HasOne<ReviewCaseRow>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<PublicationEventRow>(b =>
        {
            b.ToTable("PublicationEvents");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.CaseId, x.Version }).IsUnique();
            b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            b.HasOne<ArtifactVersionRow>().WithMany().HasForeignKey(x => new { x.CaseId, x.Version, x.ContentHash })
                .HasPrincipalKey(x => new { x.CaseId, x.Version, x.ContentHash }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private void GuardImmutableRows()
    {
        if (ChangeTracker.Entries().Any(e => (e.Entity is ArtifactVersionRow or PublicationEventRow) &&
            e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("immutable_review_record");
    }
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardImmutableRows();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardImmutableRows();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}

public sealed class ReviewDesignTimeFactory : IDesignTimeDbContextFactory<ReviewDbContext>
{
    public ReviewDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FACTORY_REVIEW_CONNECTION")
            ?? throw new InvalidOperationException("FACTORY_REVIEW_CONNECTION must be configured.");
        return new(new DbContextOptionsBuilder<ReviewDbContext>().UseNpgsql(connection).Options);
    }
}
