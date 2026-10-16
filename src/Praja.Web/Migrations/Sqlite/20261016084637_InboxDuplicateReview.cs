using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praja.Web.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class InboxDuplicateReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DuplicateOfId",
                table: "InboxMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_DuplicateOfId",
                table: "InboxMessages",
                column: "DuplicateOfId");

            migrationBuilder.AddForeignKey(
                name: "FK_InboxMessages_Grievances_DuplicateOfId",
                table: "InboxMessages",
                column: "DuplicateOfId",
                principalTable: "Grievances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InboxMessages_Grievances_DuplicateOfId",
                table: "InboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_InboxMessages_DuplicateOfId",
                table: "InboxMessages");

            migrationBuilder.DropColumn(
                name: "DuplicateOfId",
                table: "InboxMessages");
        }
    }
}
