using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praja.Web.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ContractorFieldWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ContractorAccepted",
                table: "CaseWorkflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ContractorId",
                table: "CaseWorkflows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseWorkflows_ContractorId",
                table: "CaseWorkflows",
                column: "ContractorId");

            migrationBuilder.AddForeignKey(
                name: "FK_CaseWorkflows_Operators_ContractorId",
                table: "CaseWorkflows",
                column: "ContractorId",
                principalTable: "Operators",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CaseWorkflows_Operators_ContractorId",
                table: "CaseWorkflows");

            migrationBuilder.DropIndex(
                name: "IX_CaseWorkflows_ContractorId",
                table: "CaseWorkflows");

            migrationBuilder.DropColumn(
                name: "ContractorAccepted",
                table: "CaseWorkflows");

            migrationBuilder.DropColumn(
                name: "ContractorId",
                table: "CaseWorkflows");
        }
    }
}
