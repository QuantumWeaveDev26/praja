using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praja.Web.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class CitizenIncidentEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "IncidentAccuracyMetres",
                table: "Grievances",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "IncidentLatitude",
                table: "Grievances",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "IncidentLongitude",
                table: "Grievances",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IncidentPhotoId",
                table: "Grievances",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IncidentAccuracyMetres",
                table: "Grievances");

            migrationBuilder.DropColumn(
                name: "IncidentLatitude",
                table: "Grievances");

            migrationBuilder.DropColumn(
                name: "IncidentLongitude",
                table: "Grievances");

            migrationBuilder.DropColumn(
                name: "IncidentPhotoId",
                table: "Grievances");
        }
    }
}
