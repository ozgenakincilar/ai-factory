using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// M11.3: immutable pull request outcomes for verified development jobs.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261017090000_PullRequests")]
public sealed class PullRequests : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(PullRequests).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.PullRequests.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Pull request outcomes are append-only; downgrade is not supported.");
}
