using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// M8.5b/c: decomposition-v1 with plan approval and parallel parts.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261016110000_Decomposition")]
public sealed class Decomposition : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(Decomposition).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.Decomposition.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Decomposed run history cannot be downgraded destructively.");
}
