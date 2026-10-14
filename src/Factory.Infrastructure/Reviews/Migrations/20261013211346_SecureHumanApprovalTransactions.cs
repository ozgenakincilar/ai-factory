using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Factory.Infrastructure.Reviews.Migrations
{
    /// <inheritdoc />
    public partial class SecureHumanApprovalTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "case_pending_only_5a",
                schema: "review",
                table: "ReviewCases");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ReviewCases_Id_ProjectId",
                schema: "review",
                table: "ReviewCases",
                columns: new[] { "Id", "ProjectId" });

            migrationBuilder.CreateTable(
                name: "DecisionEvents",
                schema: "review",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ProjectRevision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    PrincipalRevision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    MembershipRevision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    ExpectedRevision = table.Column<long>(type: "bigint", nullable: false),
                    ResultRevision = table.Column<long>(type: "bigint", nullable: false),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ResultState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CommandVersion = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionEvents", x => x.Id);
                    table.UniqueConstraint("AK_DecisionEvents_Id_ProjectId_PrincipalId_CommandVersion_Key_~", x => new { x.Id, x.ProjectId, x.PrincipalId, x.CommandVersion, x.Key, x.Fingerprint });
                    table.CheckConstraint("decision_command_version", "\"CommandVersion\" = 1");
                    table.CheckConstraint("decision_reason", "\"Decision\" = 'Approve' OR (\"Reason\" IS NOT NULL AND length(btrim(\"Reason\")) > 0)");
                    table.CheckConstraint("decision_result", "(\"Decision\" = 'Approve' AND \"ResultState\" = 'Approved') OR (\"Decision\" = 'Reject' AND \"ResultState\" = 'Rejected') OR (\"Decision\" = 'RequestChanges' AND \"ResultState\" = 'ChangesRequested')");
                    table.CheckConstraint("decision_revision", "\"ExpectedRevision\" > 0 AND \"ResultRevision\" = \"ExpectedRevision\" + 1");
                    table.ForeignKey(
                        name: "FK_DecisionEvents_ArtifactVersions_CaseId_Version_ContentHash",
                        columns: x => new { x.CaseId, x.Version, x.ContentHash },
                        principalSchema: "review",
                        principalTable: "ArtifactVersions",
                        principalColumns: new[] { "CaseId", "Version", "ContentHash" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DecisionEvents_ReviewCases_CaseId_ProjectId",
                        columns: x => new { x.CaseId, x.ProjectId },
                        principalSchema: "review",
                        principalTable: "ReviewCases",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommandReceipts",
                schema: "review",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandVersion = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<Guid>(type: "uuid", nullable: false),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResponseVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandReceipts", x => new { x.ProjectId, x.PrincipalId, x.CommandVersion, x.Key });
                    table.CheckConstraint("receipt_format", "\"CommandVersion\" = 1 AND \"ResponseVersion\" = 1");
                    table.ForeignKey(
                        name: "FK_CommandReceipts_DecisionEvents_DecisionId_ProjectId_Princip~",
                        columns: x => new { x.DecisionId, x.ProjectId, x.PrincipalId, x.CommandVersion, x.Key, x.Fingerprint },
                        principalSchema: "review",
                        principalTable: "DecisionEvents",
                        principalColumns: new[] { "Id", "ProjectId", "PrincipalId", "CommandVersion", "Key", "Fingerprint" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "case_state",
                schema: "review",
                table: "ReviewCases",
                sql: "\"State\" IN ('PendingReview','Approved','Rejected','ChangesRequested')");

            migrationBuilder.CreateIndex(
                name: "IX_CommandReceipts_DecisionId",
                schema: "review",
                table: "CommandReceipts",
                column: "DecisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommandReceipts_DecisionId_ProjectId_PrincipalId_CommandVer~",
                schema: "review",
                table: "CommandReceipts",
                columns: new[] { "DecisionId", "ProjectId", "PrincipalId", "CommandVersion", "Key", "Fingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionEvents_CaseId_ProjectId",
                schema: "review",
                table: "DecisionEvents",
                columns: new[] { "CaseId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionEvents_CaseId_Version",
                schema: "review",
                table: "DecisionEvents",
                columns: new[] { "CaseId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DecisionEvents_CaseId_Version_ContentHash",
                schema: "review",
                table: "DecisionEvents",
                columns: new[] { "CaseId", "Version", "ContentHash" });
            migrationBuilder.Sql(SecureHumanApprovalSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No silent destruction of approval history, including on an empty environment.
            throw new NotSupportedException("human_decision_downgrade_requires_explicit_review");
        }
    }
}
