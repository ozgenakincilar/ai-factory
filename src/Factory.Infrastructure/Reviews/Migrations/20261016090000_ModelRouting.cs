using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// M8.5a: per-attempt model routing evidence.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261016090000_ModelRouting")]
public sealed class ModelRouting : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(ModelRouting).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.ModelRouting.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Routing evidence cannot be downgraded destructively.");
}
