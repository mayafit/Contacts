using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <inheritdoc />
    public partial class RefuseV6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Two devices started a minute or two apart: re-fuse with the wider start tolerance
            migrationBuilder.Sql("UPDATE atlas.activities SET \"ClusterId\" = NULL WHERE \"ClusterId\" IS NOT NULL;");
            migrationBuilder.Sql("DELETE FROM atlas.activity_fusion;");


        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
