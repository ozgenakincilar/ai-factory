using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Factory.Infrastructure.Reviews.Migrations
{
    /// <inheritdoc />
    public partial class AlignDecisionReasonPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "decision_reason",
                schema: "review",
                table: "DecisionEvents");

            migrationBuilder.AddCheckConstraint(
                name: "decision_reason_v1",
                schema: "review",
                table: "DecisionEvents",
                sql: "(\"Reason\" IS NULL OR\r\n  pg_catalog.length(\"Reason\") + pg_catalog.length(pg_catalog.regexp_replace(\"Reason\" COLLATE \"C\", U&'[\\0001-\\FFFF]', '', 'g')) <= 4000)\r\nAND (\"Decision\" = 'Approve' OR (\"Reason\" IS NOT NULL AND\r\n  pg_catalog.length(pg_catalog.btrim(\"Reason\", U&'\\0009\\000A\\000B\\000C\\000D\\0020\\0085\\00A0\\1680\\2000\\2001\\2002\\2003\\2004\\2005\\2006\\2007\\2008\\2009\\200A\\2028\\2029\\202F\\205F\\3000')) > 0))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("reason_policy_downgrade_requires_explicit_review");
        }
    }
}
