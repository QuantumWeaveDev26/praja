using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Praja.Web.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddDemoRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CategoryCode",
                table: "Grievances",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RoutingEvidence",
                table: "Grievances",
                type: "TEXT",
                maxLength: 250,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RoutingMethod",
                table: "Grievances",
                type: "TEXT",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SubcategoryCode",
                table: "Grievances",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CategoryCode",
                table: "Grievances");

            migrationBuilder.DropColumn(
                name: "RoutingEvidence",
                table: "Grievances");

            migrationBuilder.DropColumn(
                name: "RoutingMethod",
                table: "Grievances");

            migrationBuilder.DropColumn(
                name: "SubcategoryCode",
                table: "Grievances");
        }
    }
}
