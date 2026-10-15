using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// M8.1 per-call model usage and operator-maintained prices on top of released M7.3.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261016010000_UsageMetering")]
public sealed class UsageMetering : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(UsageMetering).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.UsageMetering.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Usage history cannot be downgraded destructively.");
}
