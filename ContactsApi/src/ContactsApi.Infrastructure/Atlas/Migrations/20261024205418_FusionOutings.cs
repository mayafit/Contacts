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

            migrationBuilder.AddColumn<double>(
                name: "MaxLat",
                schema: "atlas",
                table: "activity_fusion",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MaxLon",
                schema: "atlas",
                table: "activity_fusion",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MinLat",
                schema: "atlas",
                table: "activity_fusion",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MinLon",
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

            // Outings recorded in pieces are joined now: a full re-fuse of every owner
            // through the fusion queue (retried with backoff if it fails; the current
            // grouping stays visible until the new one replaces it)
            migrationBuilder.Sql(@"INSERT INTO atlas.fusion_queue (""OwnerId"", ""From"", ""To"", ""Full"", ""Attempts"", ""NextAttemptAt"", ""CreatedAt"")
                SELECT DISTINCT ""OwnerId"", NULL::timestamptz, NULL::timestamptz, TRUE, 0, NULL::timestamptz, now() FROM atlas.activities;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationS",
                schema: "atlas",
                table: "activity_fusion");

            migrationBuilder.DropColumn(
                name: "MaxLat",
                schema: "atlas",
                table: "activity_fusion");

            migrationBuilder.DropColumn(
                name: "MaxLon",
                schema: "atlas",
                table: "activity_fusion");

            migrationBuilder.DropColumn(
                name: "MinLat",
                schema: "atlas",
                table: "activity_fusion");

            migrationBuilder.DropColumn(
                name: "MinLon",
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
