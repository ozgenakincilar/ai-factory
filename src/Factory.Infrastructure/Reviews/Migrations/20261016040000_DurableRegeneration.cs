using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// Durable regeneration of existing work items, replacing the retired synchronous M6 route.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261016040000_DurableRegeneration")]
public sealed class DurableRegeneration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(DurableRegeneration).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.DurableRegeneration.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Workflow history cannot be downgraded destructively.");
}
