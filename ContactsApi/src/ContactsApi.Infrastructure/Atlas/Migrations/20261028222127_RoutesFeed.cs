using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <inheritdoc />
    public partial class RoutesFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_activity_fusion_OwnerId",
                schema: "atlas",
                table: "activity_fusion");

            migrationBuilder.DropIndex(
                name: "IX_activities_ClusterId",
                schema: "atlas",
                table: "activities");

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                schema: "atlas",
                table: "activities",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_activity_fusion_OwnerId_UpdatedAt",
                schema: "atlas",
                table: "activity_fusion",
                columns: new[] { "OwnerId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_activities_OwnerId_ClusterId",
                schema: "atlas",
                table: "activities",
                columns: new[] { "OwnerId", "ClusterId" });

            migrationBuilder.CreateIndex(
                name: "IX_activities_OwnerId_UpdatedAt_Id",
                schema: "atlas",
                table: "activities",
                columns: new[] { "OwnerId", "UpdatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_activity_fusion_OwnerId_UpdatedAt",
                schema: "atlas",
                table: "activity_fusion");

            migrationBuilder.DropIndex(
                name: "IX_activities_OwnerId_ClusterId",
                schema: "atlas",
                table: "activities");

            migrationBuilder.DropIndex(
                name: "IX_activities_OwnerId_UpdatedAt_Id",
                schema: "atlas",
                table: "activities");

            migrationBuilder.DropColumn(
                name: "SourceType",
                schema: "atlas",
                table: "activities");

            migrationBuilder.CreateIndex(
                name: "IX_activity_fusion_OwnerId",
                schema: "atlas",
                table: "activity_fusion",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_activities_ClusterId",
                schema: "atlas",
                table: "activities",
                column: "ClusterId");
        }
    }
}
