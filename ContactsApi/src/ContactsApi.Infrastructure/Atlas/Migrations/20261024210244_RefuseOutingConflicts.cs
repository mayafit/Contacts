using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <inheritdoc />
    public partial class RefuseOutingConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Conflicts no longer list a moment's times or a phone segment's distance: re-fuse every owner
            migrationBuilder.Sql(@"INSERT INTO atlas.fusion_queue (""OwnerId"", ""From"", ""To"", ""Full"", ""Attempts"", ""NextAttemptAt"", ""CreatedAt"")
                SELECT DISTINCT ""OwnerId"", NULL::timestamptz, NULL::timestamptz, TRUE, 0, NULL::timestamptz, now() FROM atlas.activities;");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
