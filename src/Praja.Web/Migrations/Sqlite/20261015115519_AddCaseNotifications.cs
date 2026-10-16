using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praja.Web.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddCaseNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaseNotifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Recipient = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReadUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CaseNotifications_Grievances_CaseId",
                        column: x => x.CaseId,
                        principalTable: "Grievances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseNotifications_CaseId_Recipient_EventKey",
                table: "CaseNotifications",
                columns: new[] { "CaseId", "Recipient", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseNotifications_Recipient_ReadUtc",
                table: "CaseNotifications",
                columns: new[] { "Recipient", "ReadUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaseNotifications");
        }
    }
}
