using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// M7.2 waiting/decision and cancellation contracts on top of the released M7.1 migration.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261015230000_DurableWorkflowDecisions")]
public sealed class DurableWorkflowDecisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(DurableWorkflowDecisions).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.DurableWorkflowDecisions.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Workflow history cannot be downgraded destructively.");
}
