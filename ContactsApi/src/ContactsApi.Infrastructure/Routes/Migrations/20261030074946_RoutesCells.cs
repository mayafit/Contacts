using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Routes.Migrations
{
    /// <inheritdoc />
    public partial class RoutesCells : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long[]>(
                name: "Cells",
                schema: "routes",
                table: "matched_routes",
                type: "bigint[]",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Cells",
                schema: "routes",
                table: "matched_routes");
        }
    }
}
