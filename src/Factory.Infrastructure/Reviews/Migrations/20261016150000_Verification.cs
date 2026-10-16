using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// M9.3: verification jobs and immutable sandbox evidence.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261016150000_Verification")]
public sealed class Verification : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(Verification).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.Verification.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Verification evidence is append-only; downgrade is not supported.");
}
