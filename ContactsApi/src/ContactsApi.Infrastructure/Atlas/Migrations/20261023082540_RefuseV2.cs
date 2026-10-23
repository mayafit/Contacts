using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <summary>
    /// The fusion rules changed (a short activity inside a long one is no longer
    /// joined to it): clear the derived fusion layer so the startup backfill fuses
    /// everything again. Source records and the user's decisions stay.
    /// </summary>
    public partial class RefuseV2 : Migration
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
