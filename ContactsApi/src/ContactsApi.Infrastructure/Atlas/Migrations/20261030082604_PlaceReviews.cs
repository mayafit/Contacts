using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <inheritdoc />
    public partial class PlaceReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Reviews",
                schema: "atlas",
                table: "imports",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "place_reviews",
                schema: "atlas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    LinkedBy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CountryCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Lat = table.Column<double>(type: "double precision", nullable: false),
                    Lon = table.Column<double>(type: "double precision", nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    Text = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    Published = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MapsUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Answers = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_place_reviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_place_reviews_places_PlaceId",
                        column: x => x.PlaceId,
                        principalSchema: "atlas",
                        principalTable: "places",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "review_reminders",
                schema: "atlas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PlaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_reminders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_review_reminders_places_PlaceId",
                        column: x => x.PlaceId,
                        principalSchema: "atlas",
                        principalTable: "places",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_place_reviews_ImportId",
                schema: "atlas",
                table: "place_reviews",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_place_reviews_OwnerId_PlaceId",
                schema: "atlas",
                table: "place_reviews",
                columns: new[] { "OwnerId", "PlaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_place_reviews_OwnerId_ReviewedAt",
                schema: "atlas",
                table: "place_reviews",
                columns: new[] { "OwnerId", "ReviewedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_place_reviews_OwnerId_Source_ExternalId",
                schema: "atlas",
                table: "place_reviews",
                columns: new[] { "OwnerId", "Source", "ExternalId" },
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_place_reviews_PlaceId",
                schema: "atlas",
                table: "place_reviews",
                column: "PlaceId");

            migrationBuilder.CreateIndex(
                name: "IX_review_reminders_OwnerId_Category",
                schema: "atlas",
                table: "review_reminders",
                columns: new[] { "OwnerId", "Category" },
                unique: true,
                filter: "\"Category\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_review_reminders_OwnerId_PlaceId",
                schema: "atlas",
                table: "review_reminders",
                columns: new[] { "OwnerId", "PlaceId" },
                unique: true,
                filter: "\"PlaceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_review_reminders_PlaceId",
                schema: "atlas",
                table: "review_reminders",
                column: "PlaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "place_reviews",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "review_reminders",
                schema: "atlas");

            migrationBuilder.DropColumn(
                name: "Reviews",
                schema: "atlas",
                table: "imports");
        }
    }
}
