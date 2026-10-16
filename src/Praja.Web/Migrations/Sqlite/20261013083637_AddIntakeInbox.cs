using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praja.Web.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddIntakeInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalText = table.Column<string>(type: "TEXT", maxLength: 6000, nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Language = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CapturedBy = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    GrievanceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReviewNote = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ReviewedBy = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    ReviewedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboxMessages_Grievances_GrievanceId",
                        column: x => x.GrievanceId,
                        principalTable: "Grievances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InboxMessages_Operators_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Operators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_GrievanceId",
                table: "InboxMessages",
                column: "GrievanceId",
                unique: true,
                filter: "[GrievanceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_OwnerId_Fingerprint",
                table: "InboxMessages",
                columns: new[] { "OwnerId", "Fingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_OwnerId_IdempotencyKey",
                table: "InboxMessages",
                columns: new[] { "OwnerId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_OwnerId_State",
                table: "InboxMessages",
                columns: new[] { "OwnerId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxMessages");
        }
    }
}
