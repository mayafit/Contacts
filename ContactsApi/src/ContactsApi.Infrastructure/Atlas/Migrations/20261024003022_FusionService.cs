using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <inheritdoc />
    public partial class FusionService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Matching now uses per-source tolerances and track comparison: clear the
            // derived fusion layer so the startup backfill fuses everything again
            // (source records and the user's decisions stay)
            migrationBuilder.Sql("UPDATE atlas.activities SET \"ClusterId\" = NULL WHERE \"ClusterId\" IS NOT NULL;");
            migrationBuilder.Sql("DELETE FROM atlas.activity_fusion;");

            migrationBuilder.AddColumn<string>(
                name: "Evidence",
                schema: "atlas",
                table: "activity_fusion",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "MatchConfidence",
                schema: "atlas",
                table: "activity_fusion",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "fusion_queue",
                schema: "atlas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    From = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    To = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Full = table.Column<bool>(type: "boolean", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fusion_queue", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "fusion_settings",
                schema: "atlas",
                columns: table => new
                {
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fusion_settings", x => x.OwnerId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fusion_queue_OwnerId_Id",
                schema: "atlas",
                table: "fusion_queue",
                columns: new[] { "OwnerId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fusion_queue",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "fusion_settings",
                schema: "atlas");

            migrationBuilder.DropColumn(
                name: "Evidence",
                schema: "atlas",
                table: "activity_fusion");

            migrationBuilder.DropColumn(
                name: "MatchConfidence",
                schema: "atlas",
                table: "activity_fusion");
        }
    }
}
