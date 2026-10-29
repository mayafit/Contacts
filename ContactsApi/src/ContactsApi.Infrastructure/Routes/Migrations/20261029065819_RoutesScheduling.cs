using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Routes.Migrations
{
    /// <inheritdoc />
    public partial class RoutesScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SweepAfter",
                schema: "routes",
                table: "owner_cursors",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SweptAt",
                schema: "routes",
                table: "owner_cursors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cursor",
                schema: "routes",
                table: "jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SweepAfter",
                schema: "routes",
                table: "owner_cursors");

            migrationBuilder.DropColumn(
                name: "SweptAt",
                schema: "routes",
                table: "owner_cursors");

            migrationBuilder.DropColumn(
                name: "Cursor",
                schema: "routes",
                table: "jobs");
        }
    }
}
