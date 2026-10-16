using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praja.Web.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class CompletionOfficerProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CompletedBy",
                table: "CaseWorkflows",
                type: "TEXT",
                nullable: true);
            migrationBuilder.Sql("UPDATE CaseWorkflows SET CompletedBy = AssignedOfficerId WHERE CompletionPhotoId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedBy",
                table: "CaseWorkflows");
        }
    }
}
