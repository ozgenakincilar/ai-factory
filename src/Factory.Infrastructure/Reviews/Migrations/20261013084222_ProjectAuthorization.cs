using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Factory.Infrastructure.Reviews.Migrations
{
    /// <inheritdoc />
    public partial class ProjectAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "access");

            migrationBuilder.CreateTable(
                name: "Principals",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Principals", x => x.Id);
                    table.UniqueConstraint("AK_Principals_Id_Kind", x => new { x.Id, x.Kind });
                    table.CheckConstraint("principal_kind", "\"Kind\" IN ('Human', 'Service')");
                    table.CheckConstraint("principal_valid", "length(btrim(\"Issuer\")) > 0 AND length(btrim(\"Subject\")) > 0 AND \"Revision\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.CheckConstraint("project_valid", "length(btrim(\"Id\")) > 0 AND \"Revision\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "ProjectMemberships",
                schema: "access",
                columns: table => new
                {
                    ProjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PrincipalKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectMemberships", x => new { x.ProjectId, x.PrincipalId, x.Role });
                    table.CheckConstraint("membership_role", "\"Role\" IN ('Reader', 'Generator', 'Reviewer') AND \"Revision\" > 0");
                    table.CheckConstraint("reviewer_human_only", "\"Role\" <> 'Reviewer' OR \"PrincipalKind\" = 'Human'");
                    table.ForeignKey(
                        name: "FK_ProjectMemberships_Principals_PrincipalId_PrincipalKind",
                        columns: x => new { x.PrincipalId, x.PrincipalKind },
                        principalSchema: "access",
                        principalTable: "Principals",
                        principalColumns: new[] { "Id", "Kind" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectMemberships_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "access",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Principals_Issuer_Subject",
                schema: "access",
                table: "Principals",
                columns: new[] { "Issuer", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMemberships_PrincipalId_PrincipalKind",
                schema: "access",
                table: "ProjectMemberships",
                columns: new[] { "PrincipalId", "PrincipalKind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectMemberships",
                schema: "access");

            migrationBuilder.DropTable(
                name: "Principals",
                schema: "access");

            migrationBuilder.DropTable(
                name: "Projects",
                schema: "access");
        }
    }
}
