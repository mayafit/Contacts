using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Routes.Migrations
{
    /// <inheritdoc />
    public partial class RoutesRebuild : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Basis",
                schema: "routes",
                table: "matched_routes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EndLat",
                schema: "routes",
                table: "matched_routes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EndLon",
                schema: "routes",
                table: "matched_routes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Method",
                schema: "routes",
                table: "matched_routes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "auto");

            migrationBuilder.AddColumn<double>(
                name: "StartLat",
                schema: "routes",
                table: "matched_routes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "StartLon",
                schema: "routes",
                table: "matched_routes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartedAt",
                schema: "routes",
                table: "matched_routes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Result",
                schema: "routes",
                table: "jobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "route_waypoints",
                schema: "routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Points = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_route_waypoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_route_waypoints_OwnerId_SourceKey",
                schema: "routes",
                table: "route_waypoints",
                columns: new[] { "OwnerId", "SourceKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "route_waypoints",
                schema: "routes");

            migrationBuilder.DropColumn(
                name: "Basis",
                schema: "routes",
                table: "matched_routes");

            migrationBuilder.DropColumn(
                name: "EndLat",
                schema: "routes",
                table: "matched_routes");

            migrationBuilder.DropColumn(
                name: "EndLon",
                schema: "routes",
                table: "matched_routes");

            migrationBuilder.DropColumn(
                name: "Method",
                schema: "routes",
                table: "matched_routes");

            migrationBuilder.DropColumn(
                name: "StartLat",
                schema: "routes",
                table: "matched_routes");

            migrationBuilder.DropColumn(
                name: "StartLon",
                schema: "routes",
                table: "matched_routes");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                schema: "routes",
                table: "matched_routes");

            migrationBuilder.DropColumn(
                name: "Result",
                schema: "routes",
                table: "jobs");
        }
    }
}
