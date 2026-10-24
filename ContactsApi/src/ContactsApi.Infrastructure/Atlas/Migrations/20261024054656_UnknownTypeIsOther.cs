using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <summary>Google Timeline's "unknown activity type" is the type "other"</summary>
    public partial class UnknownTypeIsOther : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE atlas.activities SET \"Type\" = 'other' WHERE \"Type\" IN ('unknown', 'unknown_activity_type', 'unknown_activity');");
            migrationBuilder.Sql("UPDATE atlas.activity_fusion SET \"Type\" = 'other' WHERE \"Type\" IN ('unknown', 'unknown_activity_type', 'unknown_activity');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
