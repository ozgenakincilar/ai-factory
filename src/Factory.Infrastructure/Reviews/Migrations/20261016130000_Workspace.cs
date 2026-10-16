using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// M12a: read-only listings for the Product Workspace.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261016130000_Workspace")]
public sealed class Workspace : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(Workspace).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.Workspace.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Workspace listings are additive; downgrade is not supported.");
}
