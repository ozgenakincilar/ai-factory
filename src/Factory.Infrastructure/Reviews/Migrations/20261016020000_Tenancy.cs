using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// M8.2 organizations, organization roles and immutable project ownership on top of M8.1.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261016020000_Tenancy")]
public sealed class Tenancy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(Tenancy).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.Tenancy.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Tenant history cannot be downgraded destructively.");
}
