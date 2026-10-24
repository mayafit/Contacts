using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <summary>
    /// Matching changed (consecutive items are never one activity; two records of
    /// one source must almost fully overlap): clear the derived fusion layer so the
    /// startup backfill fuses again. Source records and user decisions stay.
    /// </summary>
    public partial class RefuseV3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE atlas.activities SET \"ClusterId\" = NULL WHERE \"ClusterId\" IS NOT NULL;");
            migrationBuilder.Sql("DELETE FROM atlas.activity_fusion;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
