using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Routes.Migrations
{
    /// <inheritdoc />
    public partial class RoutesRoads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "RoadNames",
                schema: "routes",
                table: "matched_routes",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Roads",
                schema: "routes",
                table: "matched_routes",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RoadNames",
                schema: "routes",
                table: "matched_routes");

            migrationBuilder.DropColumn(
                name: "Roads",
                schema: "routes",
                table: "matched_routes");
        }
    }
}
