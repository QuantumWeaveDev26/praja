using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praja.Web.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class CommunityReportReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReviewNote",
                table: "CommunityVerifications",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReviewState",
                table: "CommunityVerifications",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedBy",
                table: "CommunityVerifications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedUtc",
                table: "CommunityVerifications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "CommunityVerifications",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReopenedUtc",
                table: "CaseWorkflows",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewNote",
                table: "CommunityVerifications");

            migrationBuilder.DropColumn(
                name: "ReviewState",
                table: "CommunityVerifications");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                table: "CommunityVerifications");

            migrationBuilder.DropColumn(
                name: "ReviewedUtc",
                table: "CommunityVerifications");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "CommunityVerifications");

            migrationBuilder.DropColumn(
                name: "ReopenedUtc",
                table: "CaseWorkflows");
        }
    }
}
