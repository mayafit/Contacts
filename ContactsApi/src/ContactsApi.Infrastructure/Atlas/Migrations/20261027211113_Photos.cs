using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <inheritdoc />
    public partial class Photos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Cursor",
                schema: "atlas",
                table: "source_connections",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "photos",
                schema: "atlas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    MimeType = table.Column<string>(type: "text", nullable: true),
                    IsVideo = table.Column<bool>(type: "boolean", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: true),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    CameraMake = table.Column<string>(type: "text", nullable: true),
                    CameraModel = table.Column<string>(type: "text", nullable: true),
                    TakenAtRaw = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClockOffsetMin = table.Column<int>(type: "integer", nullable: false),
                    TakenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Lat = table.Column<double>(type: "double precision", nullable: true),
                    Lon = table.Column<double>(type: "double precision", nullable: true),
                    Altitude = table.Column<double>(type: "double precision", nullable: true),
                    MapLat = table.Column<double>(type: "double precision", nullable: true),
                    MapLon = table.Column<double>(type: "double precision", nullable: true),
                    PlacedBy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    VisitId = table.Column<Guid>(type: "uuid", nullable: true),
                    Placed = table.Column<bool>(type: "boolean", nullable: false),
                    WebUrl = table.Column<string>(type: "text", nullable: true),
                    Folder = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_photos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_photos_OwnerId_ActivityId",
                schema: "atlas",
                table: "photos",
                columns: new[] { "OwnerId", "ActivityId" },
                filter: "\"ActivityId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_photos_OwnerId_Placed",
                schema: "atlas",
                table: "photos",
                columns: new[] { "OwnerId", "Placed" },
                filter: "NOT \"Placed\"");

            migrationBuilder.CreateIndex(
                name: "IX_photos_OwnerId_Provider_ExternalId",
                schema: "atlas",
                table: "photos",
                columns: new[] { "OwnerId", "Provider", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_photos_OwnerId_TakenAt",
                schema: "atlas",
                table: "photos",
                columns: new[] { "OwnerId", "TakenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_photos_OwnerId_VisitId",
                schema: "atlas",
                table: "photos",
                columns: new[] { "OwnerId", "VisitId" },
                filter: "\"VisitId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "photos",
                schema: "atlas");

            migrationBuilder.DropColumn(
                name: "Cursor",
                schema: "atlas",
                table: "source_connections");
        }
    }
}
