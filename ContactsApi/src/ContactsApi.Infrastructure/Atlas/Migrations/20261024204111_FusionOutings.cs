using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <inheritdoc />
    public partial class FusionOutings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DurationS",
                schema: "atlas",
                table: "activity_fusion",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Polyline",
                schema: "atlas",
                table: "activity_fusion",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SegmentCount",
                schema: "atlas",
                table: "activity_fusion",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Outings recorded in pieces are joined now: re-fuse everyone (the startup
            // backfill does it when activity_fusion is empty; user decisions are kept)
            migrationBuilder.Sql("UPDATE atlas.activities SET \"ClusterId\" = NULL WHERE \"ClusterId\" IS NOT NULL;");
            migrationBuilder.Sql("DELETE FROM atlas.activity_fusion;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationS",
                schema: "atlas",
                table: "activity_fusion");

            migrationBuilder.DropColumn(
                name: "Polyline",
                schema: "atlas",
                table: "activity_fusion");

            migrationBuilder.DropColumn(
                name: "SegmentCount",
                schema: "atlas",
                table: "activity_fusion");
        }
    }
}
