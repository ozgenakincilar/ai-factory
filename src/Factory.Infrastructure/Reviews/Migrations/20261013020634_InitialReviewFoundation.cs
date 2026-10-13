using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Factory.Infrastructure.Reviews.Migrations
{
    /// <inheritdoc />
    public partial class InitialReviewFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "review");

            migrationBuilder.CreateTable(
                name: "ReviewCases",
                schema: "review",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CurrentVersion = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewCases", x => x.Id);
                    table.CheckConstraint("case_pending_only_5a", "\"State\" = 'PendingReview'");
                    table.CheckConstraint("case_project_required", "length(btrim(\"ProjectId\")) > 0");
                    table.CheckConstraint("case_version_positive", "\"CurrentVersion\" > 0 AND \"Revision\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "ArtifactVersions",
                schema: "review",
                columns: table => new
                {
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtifactVersions", x => new { x.CaseId, x.Version });
                    table.UniqueConstraint("AK_ArtifactVersions_CaseId_Version_ContentHash", x => new { x.CaseId, x.Version, x.ContentHash });
                    table.CheckConstraint("artifact_hash_matches", "\"ContentHash\" = encode(sha256(\"Content\"), 'hex')");
                    table.CheckConstraint("artifact_version_positive", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_ArtifactVersions_ReviewCases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "review",
                        principalTable: "ReviewCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PublicationEvents",
                schema: "review",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationEvents_ArtifactVersions_CaseId_Version_ContentHa~",
                        columns: x => new { x.CaseId, x.Version, x.ContentHash },
                        principalSchema: "review",
                        principalTable: "ArtifactVersions",
                        principalColumns: new[] { "CaseId", "Version", "ContentHash" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEvents_CaseId_Version",
                schema: "review",
                table: "PublicationEvents",
                columns: new[] { "CaseId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationEvents_CaseId_Version_ContentHash",
                schema: "review",
                table: "PublicationEvents",
                columns: new[] { "CaseId", "Version", "ContentHash" });
            migrationBuilder.Sql("""
                ALTER TABLE review."ReviewCases" ADD CONSTRAINT case_current_artifact
                    FOREIGN KEY ("Id", "CurrentVersion") REFERENCES review."ArtifactVersions" ("CaseId", "Version")
                    DEFERRABLE INITIALLY DEFERRED;
                CREATE FUNCTION review.reject_immutable_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'immutable_review_record' USING ERRCODE = '55000'; END;
                $$;
                CREATE TRIGGER artifacts_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON review."ArtifactVersions"
                    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();
                CREATE TRIGGER events_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON review."PublicationEvents"
                    FOR EACH STATEMENT EXECUTE FUNCTION review.reject_immutable_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE review.\"ReviewCases\" DROP CONSTRAINT case_current_artifact;");
            migrationBuilder.DropTable(
                name: "PublicationEvents",
                schema: "review");

            migrationBuilder.DropTable(
                name: "ArtifactVersions",
                schema: "review");

            migrationBuilder.DropTable(
                name: "ReviewCases",
                schema: "review");
            migrationBuilder.Sql("DROP FUNCTION review.reject_immutable_change();");
        }
    }
}
