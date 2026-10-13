using Factory.Core.Authorization;
using Factory.Infrastructure.Reviews;
using Microsoft.EntityFrameworkCore;

namespace Factory.Infrastructure.Authorization;

public sealed class ProjectRow
{
    public string Id { get; set; } = "";
    public bool IsActive { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class PrincipalRow
{
    public Guid Id { get; set; }
    public string Issuer { get; set; } = "";
    public string Subject { get; set; } = "";
    public PrincipalKind Kind { get; set; }
    public bool IsActive { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class ProjectMembershipRow
{
    public string ProjectId { get; set; } = "";
    public Guid PrincipalId { get; set; }
    public PrincipalKind PrincipalKind { get; set; }
    public ProjectRole Role { get; set; }
    public bool IsActive { get; set; }
    public long Revision { get; set; } = 1;
}

public static class AccessModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<ProjectRow>(b =>
        {
            b.ToTable("Projects", "access", t => t.HasCheckConstraint("project_valid", "length(btrim(\"Id\")) > 0 AND \"Revision\" > 0"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasMaxLength(200);
            b.Property(x => x.Revision).IsConcurrencyToken();
        });
        model.Entity<PrincipalRow>(b =>
        {
            b.ToTable("Principals", "access", t =>
            {
                t.HasCheckConstraint("principal_valid", "length(btrim(\"Issuer\")) > 0 AND length(btrim(\"Subject\")) > 0 AND \"Revision\" > 0");
                t.HasCheckConstraint("principal_kind", "\"Kind\" IN ('Human', 'Service')");
            });
            b.HasKey(x => x.Id);
            b.HasAlternateKey(x => new { x.Id, x.Kind });
            b.HasIndex(x => new { x.Issuer, x.Subject }).IsUnique();
            b.Property(x => x.Issuer).HasMaxLength(512).IsRequired();
            b.Property(x => x.Subject).HasMaxLength(255).IsRequired();
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            b.Property(x => x.Revision).IsConcurrencyToken();
        });
        model.Entity<ProjectMembershipRow>(b =>
        {
            b.ToTable("ProjectMemberships", "access", t =>
            {
                t.HasCheckConstraint("membership_role", "\"Role\" IN ('Reader', 'Generator', 'Reviewer') AND \"Revision\" > 0");
                t.HasCheckConstraint("reviewer_human_only", "\"Role\" <> 'Reviewer' OR \"PrincipalKind\" = 'Human'");
            });
            b.HasKey(x => new { x.ProjectId, x.PrincipalId, x.Role });
            b.Property(x => x.ProjectId).HasMaxLength(200);
            b.Property(x => x.PrincipalKind).HasConversion<string>().HasMaxLength(16);
            b.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.HasOne<ProjectRow>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PrincipalRow>().WithMany().HasForeignKey(x => new { x.PrincipalId, x.PrincipalKind })
                .HasPrincipalKey(x => new { x.Id, x.Kind }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class ProjectAccessStore(DbContextOptions<ReviewDbContext> options) : IProjectAccessStore
{
    public async Task<bool> HasAccessAsync(TrustedIdentity identity, string projectId, ProjectRole role,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(role) || !Enum.IsDefined(identity.Kind) ||
            (role == ProjectRole.Reviewer && identity.Kind != PrincipalKind.Human)) return false;
        await using var db = new ReviewDbContext(options);
        return await (from membership in db.Set<ProjectMembershipRow>().AsNoTracking()
                      join principal in db.Set<PrincipalRow>() on membership.PrincipalId equals principal.Id
                      join project in db.Set<ProjectRow>() on membership.ProjectId equals project.Id
                      where project.Id == projectId && project.IsActive && membership.IsActive && principal.IsActive
                          && principal.Issuer == identity.Issuer && principal.Subject == identity.Subject
                          && principal.Kind == identity.Kind && membership.PrincipalKind == identity.Kind
                          && membership.Role == role
                      select membership).AnyAsync(cancellationToken);
    }
}
