using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Routes.Migrations
{
    /// <inheritdoc />
    public partial class RoutesMatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_route_quality_OwnerId_NeedsImprovement_SourceUpdatedAt",
                schema: "routes",
                table: "route_quality",
                columns: new[] { "OwnerId", "NeedsImprovement", "SourceUpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_matched_routes_OwnerId_NextAttemptAt",
                schema: "routes",
                table: "matched_routes",
                columns: new[] { "OwnerId", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_route_quality_OwnerId_NeedsImprovement_SourceUpdatedAt",
                schema: "routes",
                table: "route_quality");

            migrationBuilder.DropIndex(
                name: "IX_matched_routes_OwnerId_NextAttemptAt",
                schema: "routes",
                table: "matched_routes");
        }
    }
}
