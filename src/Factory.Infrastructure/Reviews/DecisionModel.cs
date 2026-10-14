using Factory.Core.Reviews;
using Microsoft.EntityFrameworkCore;

namespace Factory.Infrastructure.Reviews;

public sealed class DecisionEventRow
{
    public Guid Id { get; set; }
    public string ProjectId { get; set; } = "";
    public Guid CaseId { get; set; }
    public int Version { get; set; }
    public string ContentHash { get; set; } = "";
    public Guid PrincipalId { get; set; }
    public string Issuer { get; set; } = "";
    public string Subject { get; set; } = "";
    public long ProjectRevision { get; set; }
    public long PrincipalRevision { get; set; }
    public long MembershipRevision { get; set; }
    public long ExpectedRevision { get; set; }
    public long ResultRevision { get; set; }
    public ReviewDecision Decision { get; set; }
    public string? Reason { get; set; }
    public ReviewState ResultState { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int CommandVersion { get; set; } = 1;
    public Guid Key { get; set; }
    public string Fingerprint { get; set; } = "";
}

// Receipt result is the immutable event's format-1 projection, not a second mutable JSON copy.
public sealed class CommandReceiptRow
{
    public string ProjectId { get; set; } = "";
    public Guid PrincipalId { get; set; }
    public int CommandVersion { get; set; } = 1;
    public Guid Key { get; set; }
    public Guid DecisionId { get; set; }
    public string Fingerprint { get; set; } = "";
    public int ResponseVersion { get; set; } = 1;
}

internal static class DecisionModel
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<ReviewCaseRow>().HasAlternateKey(x => new { x.Id, x.ProjectId });
        model.Entity<DecisionEventRow>(b =>
        {
            b.ToTable("DecisionEvents", "review", t =>
            {
                t.HasCheckConstraint("decision_revision", "\"ExpectedRevision\" > 0 AND \"ResultRevision\" = \"ExpectedRevision\" + 1");
                t.HasCheckConstraint("decision_result", "(\"Decision\" = 'Approve' AND \"ResultState\" = 'Approved') OR (\"Decision\" = 'Reject' AND \"ResultState\" = 'Rejected') OR (\"Decision\" = 'RequestChanges' AND \"ResultState\" = 'ChangesRequested')");
                // Explicit whitespace and UTF-16 length policy. No POSIX whitespace categories.
                t.HasCheckConstraint("decision_reason_v1", """
                    ("Reason" IS NULL OR
                      pg_catalog.length("Reason") + pg_catalog.length(pg_catalog.regexp_replace("Reason" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 4000)
                    AND ("Decision" = 'Approve' OR ("Reason" IS NOT NULL AND
                      pg_catalog.length(pg_catalog.btrim("Reason", U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000')) > 0))
                    """);
                t.HasCheckConstraint("decision_command_version", "\"CommandVersion\" = 1");
            });
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.CaseId, x.Version }).IsUnique();
            b.HasAlternateKey(x => new { x.Id, x.ProjectId, x.PrincipalId, x.CommandVersion, x.Key, x.Fingerprint });
            b.Property(x => x.ProjectId).HasMaxLength(200);
            b.Property(x => x.ContentHash).HasMaxLength(64);
            b.Property(x => x.Issuer).HasMaxLength(512);
            b.Property(x => x.Subject).HasMaxLength(255);
            b.Property(x => x.Reason).HasMaxLength(4000);
            b.Property(x => x.Decision).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.ResultState).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.CreatedAt).ValueGeneratedOnAdd();
            b.Property(x => x.Fingerprint).HasMaxLength(64).ValueGeneratedOnAdd();
            b.Property(x => x.ProjectRevision).HasDefaultValue(0L).ValueGeneratedOnAdd();
            b.Property(x => x.PrincipalRevision).HasDefaultValue(0L).ValueGeneratedOnAdd();
            b.Property(x => x.MembershipRevision).HasDefaultValue(0L).ValueGeneratedOnAdd();
            b.HasOne<ReviewCaseRow>().WithMany().HasForeignKey(x => new { x.CaseId, x.ProjectId })
                .HasPrincipalKey(x => new { x.Id, x.ProjectId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ArtifactVersionRow>().WithMany().HasForeignKey(x => new { x.CaseId, x.Version, x.ContentHash })
                .HasPrincipalKey(x => new { x.CaseId, x.Version, x.ContentHash }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<CommandReceiptRow>(b =>
        {
            b.ToTable("CommandReceipts", "review", t => t.HasCheckConstraint("receipt_format", "\"CommandVersion\" = 1 AND \"ResponseVersion\" = 1"));
            b.HasKey(x => new { x.ProjectId, x.PrincipalId, x.CommandVersion, x.Key });
            b.HasIndex(x => x.DecisionId).IsUnique();
            b.Property(x => x.ProjectId).HasMaxLength(200);
            b.Property(x => x.Fingerprint).HasMaxLength(64);
            b.HasOne<DecisionEventRow>().WithMany().HasForeignKey(x => new { x.DecisionId, x.ProjectId, x.PrincipalId, x.CommandVersion, x.Key, x.Fingerprint })
                .HasPrincipalKey(x => new { x.Id, x.ProjectId, x.PrincipalId, x.CommandVersion, x.Key, x.Fingerprint }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
