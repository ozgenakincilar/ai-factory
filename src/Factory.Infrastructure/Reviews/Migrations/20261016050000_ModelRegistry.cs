using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// M8.4a customer-registered models, project assignments and run model snapshots.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261016050000_ModelRegistry")]
public sealed class ModelRegistry : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(ModelRegistry).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.ModelRegistry.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Model registry history cannot be downgraded destructively.");
}
