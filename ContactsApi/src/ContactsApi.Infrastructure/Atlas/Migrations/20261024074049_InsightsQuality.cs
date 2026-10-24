using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <inheritdoc />
    public partial class InsightsQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Google types that slipped through, and heart rate only from real series:
            // map the types, then clear the derived fusion layer so the startup
            // backfill fuses again (source records and user decisions stay)
            migrationBuilder.Sql("UPDATE atlas.activities SET \"Type\" = 'transit' WHERE \"Type\" IN ('in_gondola_lift', 'in_cable_car', 'in_taxi', 'in_vehicle_ferry');");
            migrationBuilder.Sql("UPDATE atlas.activities SET \"Type\" = 'other' WHERE \"Type\" IN ('boating', 'sailing', 'kayaking', 'rowing', 'paddling', 'surfing');");
            migrationBuilder.Sql("UPDATE atlas.activities SET \"ClusterId\" = NULL WHERE \"ClusterId\" IS NOT NULL;");
            migrationBuilder.Sql("DELETE FROM atlas.activity_fusion;");


        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
