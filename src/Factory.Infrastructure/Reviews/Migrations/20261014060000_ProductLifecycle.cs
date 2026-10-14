using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// Lifecycle operations are SQL function contracts, not tracked EF entities. Like the existing
// deferred guards, their schema is reviewed/tested explicitly in addition to EF model drift.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261014060000_ProductLifecycle")]
public sealed class ProductLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(ProductLifecycle).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.ProductLifecycle.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Lifecycle history cannot be downgraded destructively.");
}
