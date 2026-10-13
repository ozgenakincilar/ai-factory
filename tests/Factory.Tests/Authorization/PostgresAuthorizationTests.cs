using Factory.Core.Authorization;
using Factory.Infrastructure.Authorization;
using Factory.Infrastructure.Reviews;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Factory.Tests.Authorization;

public sealed class AuthorizationPostgresFactAttribute : FactAttribute
{
    public AuthorizationPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_AUTH_OWNER_CONNECTION")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_AUTH_READER_CONNECTION")))
            Skip = "Real PostgreSQL owner and restricted authorization reader required.";
    }
}

public class PostgresAuthorizationTests
{
    internal static DbContextOptions<ReviewDbContext> Options(string variable) => new DbContextOptionsBuilder<ReviewDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable(variable)!).Options;
    internal static async Task<(string Project, PrincipalRow Principal)> Seed(PrincipalKind kind = PrincipalKind.Human)
    {
        await using var db = new ReviewDbContext(Options("FACTORY_AUTH_OWNER_CONNECTION"));
        var project = "auth-test-" + Guid.NewGuid();
        var principal = new PrincipalRow { Id = Guid.NewGuid(), Issuer = "https://test.factory.invalid", Subject = Guid.NewGuid().ToString(), Kind = kind, IsActive = true };
        db.Add(new ProjectRow { Id = project, IsActive = true }); db.Add(principal);
        db.Add(new ProjectMembershipRow { ProjectId = project, PrincipalId = principal.Id, PrincipalKind = kind, Role = ProjectRole.Generator, IsActive = true });
        await db.SaveChangesAsync();
        return (project, principal);
    }

    [AuthorizationPostgresFact]
    public async Task Memberships_RequireExactIdentityProjectKindAndActiveRecords()
    {
        var (project, principal) = await Seed();
        var store = new ProjectAccessStore(Options("FACTORY_AUTH_READER_CONNECTION"));
        var identity = new TrustedIdentity(principal.Issuer, principal.Subject, principal.Kind);
        Assert.True(await store.HasAccessAsync(identity, project, ProjectRole.Generator));
        Assert.False(await store.HasAccessAsync(identity, "other", ProjectRole.Generator));
        Assert.False(await store.HasAccessAsync(identity with { Issuer = "other" }, project, ProjectRole.Generator));
        Assert.False(await store.HasAccessAsync(identity with { Kind = PrincipalKind.Service }, project, ProjectRole.Generator));
        Assert.False(await store.HasAccessAsync(identity, project, ProjectRole.Reviewer));
        await using var db = new ReviewDbContext(Options("FACTORY_AUTH_OWNER_CONNECTION"));
        db.Add(new ProjectMembershipRow { ProjectId = project, PrincipalId = principal.Id, PrincipalKind = PrincipalKind.Human,
            Role = ProjectRole.Reviewer, IsActive = true });
        await db.SaveChangesAsync();
        Assert.True(await store.HasAccessAsync(identity, project, ProjectRole.Reviewer));
        var membership = await db.Set<ProjectMembershipRow>().SingleAsync(x => x.ProjectId == project && x.Role == ProjectRole.Generator);
        membership.IsActive = false; membership.Revision++; await db.SaveChangesAsync();
        Assert.False(await store.HasAccessAsync(identity, project, ProjectRole.Generator));
        membership.IsActive = true; membership.Revision++;
        var row = await db.Set<PrincipalRow>().SingleAsync(x => x.Id == principal.Id);
        row.IsActive = false; row.Revision++; await db.SaveChangesAsync();
        Assert.False(await store.HasAccessAsync(identity, project, ProjectRole.Generator));
        row.IsActive = true; row.Revision++;
        var p = await db.Set<ProjectRow>().SingleAsync(x => x.Id == project);
        p.IsActive = false; p.Revision++; await db.SaveChangesAsync();
        Assert.False(await store.HasAccessAsync(identity, project, ProjectRole.Generator));
    }

    [AuthorizationPostgresFact]
    public async Task DatabaseRejectsServiceReviewer_AndForgedPrincipalKind()
    {
        var (project, principal) = await Seed(PrincipalKind.Service);
        var store = new ProjectAccessStore(Options("FACTORY_AUTH_READER_CONNECTION"));
        Assert.True(await store.HasAccessAsync(new(principal.Issuer, principal.Subject, PrincipalKind.Service), project, ProjectRole.Generator));
        Assert.False(await store.HasAccessAsync(new(principal.Issuer, principal.Subject, PrincipalKind.Service), project, ProjectRole.Reviewer));
        foreach (var kind in new[] { PrincipalKind.Service, PrincipalKind.Human })
        {
            await using var db = new ReviewDbContext(Options("FACTORY_AUTH_OWNER_CONNECTION"));
            db.Add(new ProjectMembershipRow { ProjectId = project, PrincipalId = principal.Id, PrincipalKind = kind, Role = ProjectRole.Reviewer, IsActive = true });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(kind == PrincipalKind.Service ? PostgresErrorCodes.CheckViolation : PostgresErrorCodes.ForeignKeyViolation,
                Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
    }

    [AuthorizationPostgresFact]
    public async Task ReaderCannotWriteEscalateOrReadReviewArtifacts()
    {
        await using var db = new ReviewDbContext(Options("FACTORY_AUTH_READER_CONNECTION"));
        foreach (var sql in new[] { "UPDATE access.\"Principals\" SET \"IsActive\" = true", "DELETE FROM access.\"ProjectMemberships\"",
            "INSERT INTO access.\"Projects\" VALUES ('forged',true,1)", "ALTER TABLE access.\"Principals\" DISABLE TRIGGER ALL",
            "SELECT * FROM review.\"ArtifactVersions\"", "CREATE TABLE access.forged (id int)" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [AuthorizationPostgresFact]
    public async Task OperatorConcurrency_RejectsStaleMembershipUpdate()
    {
        var (project, _) = await Seed();
        await using var a = new ReviewDbContext(Options("FACTORY_AUTH_OWNER_CONNECTION"));
        await using var b = new ReviewDbContext(Options("FACTORY_AUTH_OWNER_CONNECTION"));
        var first = await a.Set<ProjectMembershipRow>().SingleAsync(x => x.ProjectId == project);
        var stale = await b.Set<ProjectMembershipRow>().SingleAsync(x => x.ProjectId == project);
        first.IsActive = false; first.Revision++; await a.SaveChangesAsync();
        stale.Revision++;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
    }
}
