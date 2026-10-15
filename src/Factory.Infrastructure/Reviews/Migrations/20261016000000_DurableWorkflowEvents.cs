using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// M7.3 commit-time run notifications and watch authorization on top of released M7.2.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261016000000_DurableWorkflowEvents")]
public sealed class DurableWorkflowEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(DurableWorkflowEvents).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.DurableWorkflowEvents.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Workflow history cannot be downgraded destructively.");
}
