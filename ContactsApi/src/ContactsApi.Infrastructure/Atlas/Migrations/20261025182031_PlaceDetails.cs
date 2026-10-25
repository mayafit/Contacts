using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <inheritdoc />
    public partial class PlaceDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Details",
                schema: "atlas",
                table: "places",
                type: "text",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "LinkedRef",
                schema: "atlas",
                table: "places",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OpeningHours",
                schema: "atlas",
                table: "places",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                schema: "atlas",
                table: "places",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Website",
                schema: "atlas",
                table: "places",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Details",
                schema: "atlas",
                table: "places");

            migrationBuilder.DropColumn(
                name: "LinkedRef",
                schema: "atlas",
                table: "places");

            migrationBuilder.DropColumn(
                name: "OpeningHours",
                schema: "atlas",
                table: "places");

            migrationBuilder.DropColumn(
                name: "Phone",
                schema: "atlas",
                table: "places");

            migrationBuilder.DropColumn(
                name: "Website",
                schema: "atlas",
                table: "places");
        }
    }
}
