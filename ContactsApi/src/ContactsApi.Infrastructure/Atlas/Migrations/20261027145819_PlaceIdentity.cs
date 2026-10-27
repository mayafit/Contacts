using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <inheritdoc />
    public partial class PlaceIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdentifiedFrom",
                schema: "atlas",
                table: "places",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentifyCandidates",
                schema: "atlas",
                table: "places",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "IdentifyState",
                schema: "atlas",
                table: "places",
                type: "text",
                nullable: false,
                defaultValue: "pending");

            // Named places (by the user, or with a real name from the source) need no identification;
            // unnamed ones wait for the background identification
            migrationBuilder.Sql(@"UPDATE atlas.places SET ""IdentifyState"" = 'user'
                WHERE ""UserEdited"" OR ""Name"" NOT IN ('', 'Place', 'Saved place', 'Searched place');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IdentifiedFrom",
                schema: "atlas",
                table: "places");

            migrationBuilder.DropColumn(
                name: "IdentifyCandidates",
                schema: "atlas",
                table: "places");

            migrationBuilder.DropColumn(
                name: "IdentifyState",
                schema: "atlas",
                table: "places");
        }
    }
}
