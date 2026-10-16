using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praja.Web.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class CommunityFeedbackRounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CommunityVerifications_CaseId_BrowserHash",
                table: "CommunityVerifications");

            migrationBuilder.AddColumn<int>(
                name: "FeedbackRound",
                table: "CommunityVerifications",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "FeedbackRound",
                table: "CaseWorkflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            // Older workspaces did not track rounds. Preserve every report, separating
            // reports before the latest recorded reopening from the current repair.
            migrationBuilder.Sql("""
                UPDATE CaseWorkflows SET FeedbackRound = 2 WHERE ReopenedUtc IS NOT NULL;
                UPDATE CommunityVerifications SET FeedbackRound = 2
                WHERE EXISTS (
                    SELECT 1 FROM CaseWorkflows w
                    WHERE w.Id = CommunityVerifications.CaseId
                      AND w.ReopenedUtc IS NOT NULL
                      AND CommunityVerifications.CreatedUtc >= w.ReopenedUtc
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CommunityVerifications_CaseId_BrowserHash_FeedbackRound",
                table: "CommunityVerifications",
                columns: new[] { "CaseId", "BrowserHash", "FeedbackRound" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CommunityVerifications_CaseId_BrowserHash_FeedbackRound",
                table: "CommunityVerifications");

            migrationBuilder.DropColumn(
                name: "FeedbackRound",
                table: "CommunityVerifications");

            migrationBuilder.DropColumn(
                name: "FeedbackRound",
                table: "CaseWorkflows");

            migrationBuilder.CreateIndex(
                name: "IX_CommunityVerifications_CaseId_BrowserHash",
                table: "CommunityVerifications",
                columns: new[] { "CaseId", "BrowserHash" },
                unique: true);
        }
    }
}
