using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Factory.Infrastructure.Reviews.Migrations;

// M8.4b: customer-registered Anthropic models.
[DbContext(typeof(ReviewDbContext))]
[Migration("20261016070000_AnthropicModels")]
public sealed class AnthropicModels : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(AnthropicModels).Assembly.GetManifestResourceStream(
            "Factory.Infrastructure.Reviews.Migrations.AnthropicModels.sql")!;
        using var reader = new StreamReader(stream);
        migrationBuilder.Sql(reader.ReadToEnd());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Model registry history cannot be downgraded destructively.");
}
