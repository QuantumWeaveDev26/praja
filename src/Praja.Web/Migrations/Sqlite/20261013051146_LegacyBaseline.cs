using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praja.Web.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class LegacyBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Operators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BootstrapSlot = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    FailedLogins = table.Column<int>(type: "INTEGER", nullable: false),
                    LockedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Operators", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Grievances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    OriginalText = table.Column<string>(type: "TEXT", maxLength: 6000, nullable: false),
                    DraftText = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    District = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Language = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Department = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    RoutingReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    DraftMethod = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsSample = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    ResolutionReport = table.Column<string>(type: "TEXT", maxLength: 6000, nullable: false),
                    EvidenceReference = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CitizenFeedback = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Grievances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Grievances_Operators_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Operators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GrievanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditEntries_Grievances_GrievanceId",
                        column: x => x.GrievanceId,
                        principalTable: "Grievances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_GrievanceId",
                table: "AuditEntries",
                column: "GrievanceId");

            migrationBuilder.CreateIndex(
                name: "IX_Grievances_OwnerId_Status",
                table: "Grievances",
                columns: new[] { "OwnerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Grievances_Reference",
                table: "Grievances",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Operators_BootstrapSlot",
                table: "Operators",
                column: "BootstrapSlot",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Operators_Email",
                table: "Operators",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "Grievances");

            migrationBuilder.DropTable(
                name: "Operators");
        }
    }
}
