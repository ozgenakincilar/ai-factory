using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// Durable workflow operations are SQL function contracts, not tracked EF entities, like M6.
// Their schema, guards and grants are tested explicitly in addition to EF model drift.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261015200000_DurableWorkflows")]
public sealed class DurableWorkflows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(DurableWorkflows).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.DurableWorkflows.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Workflow history cannot be downgraded destructively.");
}
