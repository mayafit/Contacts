using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <inheritdoc />
    public partial class TrackSpanTimes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // An activity covers its whole track: Sports Tracker gave start + moving
            // time, so rides ended before their pauses and second halves. Then the
            // derived fusion layer is cleared so the startup backfill fuses again.
            migrationBuilder.Sql(@"
                UPDATE atlas.activities a
                   SET ""StartTime"" = LEAST(a.""StartTime"", p.first), ""EndTime"" = GREATEST(a.""EndTime"", p.last)
                  FROM (SELECT ""ActivityId"", min(""Time"") AS first, max(""Time"") AS last
                          FROM atlas.track_points GROUP BY ""ActivityId"") p
                 WHERE p.""ActivityId"" = a.""Id"" AND (p.first < a.""StartTime"" OR p.last > a.""EndTime"");");
            migrationBuilder.Sql("UPDATE atlas.activities SET \"ClusterId\" = NULL WHERE \"ClusterId\" IS NOT NULL;");
            migrationBuilder.Sql("DELETE FROM atlas.activity_fusion;");


        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
