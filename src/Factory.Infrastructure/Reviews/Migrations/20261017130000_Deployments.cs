using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// M13A.2: deployment targets and durable deployments with plan-bound human approval.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261017130000_Deployments")]
public sealed class Deployments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(Deployments).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.Deployments.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Deployments are append-only; downgrade is not supported.");
}
