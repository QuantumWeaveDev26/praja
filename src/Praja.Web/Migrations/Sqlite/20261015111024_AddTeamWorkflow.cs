using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praja.Web.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddTeamWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaseWorkflows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssignedOfficerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DueUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    AccessVersion = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompletionPhotoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Latitude = table.Column<double>(type: "REAL", nullable: true),
                    Longitude = table.Column<double>(type: "REAL", nullable: true),
                    CapturedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletionNote = table.Column<string>(type: "TEXT", maxLength: 3000, nullable: false),
                    VerifiedBy = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseWorkflows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseWorkflows_Grievances_Id",
                        column: x => x.Id,
                        principalTable: "Grievances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CaseWorkflows_Operators_AssignedOfficerId",
                        column: x => x.AssignedOfficerId,
                        principalTable: "Operators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OfficerRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Department = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficerRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OfficerRoles_Operators_Id",
                        column: x => x.Id,
                        principalTable: "Operators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowUpdates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowUpdates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowUpdates_CaseWorkflows_CaseId",
                        column: x => x.CaseId,
                        principalTable: "CaseWorkflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseWorkflows_AssignedOfficerId",
                table: "CaseWorkflows",
                column: "AssignedOfficerId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowUpdates_CaseId",
                table: "WorkflowUpdates",
                column: "CaseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfficerRoles");

            migrationBuilder.DropTable(
                name: "WorkflowUpdates");

            migrationBuilder.DropTable(
                name: "CaseWorkflows");
        }
    }
}
