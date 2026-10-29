using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Routes.Migrations
{
    /// <inheritdoc />
    public partial class RoutesStoredLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DensityPolyline",
                schema: "routes",
                table: "matched_routes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ListPolyline",
                schema: "routes",
                table: "matched_routes",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DensityPolyline",
                schema: "routes",
                table: "matched_routes");

            migrationBuilder.DropColumn(
                name: "ListPolyline",
                schema: "routes",
                table: "matched_routes");
        }
    }
}
