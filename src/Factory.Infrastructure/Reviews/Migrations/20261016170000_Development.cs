using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// M10.3: development jobs and immutable Developer Agent evidence.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261016170000_Development")]
public sealed class Development : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(Development).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.Development.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Development evidence is append-only; downgrade is not supported.");
}
