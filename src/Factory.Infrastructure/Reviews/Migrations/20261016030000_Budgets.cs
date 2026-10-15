using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// M8.3 budgets and pre-call cost reservations on top of M8.2.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261016030000_Budgets")]
public sealed class Budgets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(Budgets).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.Budgets.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Budget history cannot be downgraded destructively.");
}
