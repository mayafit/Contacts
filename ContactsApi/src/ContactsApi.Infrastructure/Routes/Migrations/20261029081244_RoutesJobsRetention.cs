using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Routes.Migrations
{
    /// <inheritdoc />
    public partial class RoutesJobsRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_jobs_open_interactive",
                schema: "routes",
                table: "jobs",
                columns: new[] { "OwnerId", "NextAttemptAt" },
                filter: "\"Priority\" = 'interactive' AND \"State\" IN ('queued', 'running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_jobs_open_interactive",
                schema: "routes",
                table: "jobs");
        }
    }
}
