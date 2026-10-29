using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Routes.Migrations
{
    /// <inheritdoc />
    public partial class RoutesInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "routes");

            migrationBuilder.CreateTable(
                name: "jobs",
                schema: "routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Priority = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Scope = table.Column<string>(type: "jsonb", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    Done = table.Column<int>(type: "integer", nullable: false),
                    Counts = table.Column<string>(type: "jsonb", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "matched_routes",
                schema: "routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourcePointCount = table.Column<int>(type: "integer", nullable: false),
                    FusedType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Costing = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Engine = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EngineVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    GraphVersion = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RuleVersion = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Polyline = table.Column<string>(type: "text", nullable: true),
                    Pieces = table.Column<string>(type: "jsonb", nullable: false),
                    MatchedDistanceM = table.Column<double>(type: "double precision", nullable: true),
                    MetricsAfter = table.Column<string>(type: "jsonb", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matched_routes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "owner_cursors",
                schema: "routes",
                columns: table => new
                {
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Cursor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_owner_cursors", x => x.OwnerId);
                });

            migrationBuilder.CreateTable(
                name: "route_decisions",
                schema: "routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_route_decisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "route_quality",
                schema: "routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PointCount = table.Column<int>(type: "integer", nullable: false),
                    TrackM = table.Column<double>(type: "double precision", nullable: true),
                    Metrics = table.Column<string>(type: "jsonb", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: true),
                    Grade = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Flags = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NeedsImprovement = table.Column<bool>(type: "boolean", nullable: false),
                    Matchable = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    RuleVersion = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_route_quality", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                schema: "routes",
                columns: table => new
                {
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.OwnerId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_OwnerId_State_CreatedAt",
                schema: "routes",
                table: "jobs",
                columns: new[] { "OwnerId", "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_matched_routes_OwnerId_ActivityId",
                schema: "routes",
                table: "matched_routes",
                columns: new[] { "OwnerId", "ActivityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_route_decisions_OwnerId_SourceKey",
                schema: "routes",
                table: "route_decisions",
                columns: new[] { "OwnerId", "SourceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_route_quality_OwnerId_ActivityId",
                schema: "routes",
                table: "route_quality",
                columns: new[] { "OwnerId", "ActivityId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "jobs",
                schema: "routes");

            migrationBuilder.DropTable(
                name: "matched_routes",
                schema: "routes");

            migrationBuilder.DropTable(
                name: "owner_cursors",
                schema: "routes");

            migrationBuilder.DropTable(
                name: "route_decisions",
                schema: "routes");

            migrationBuilder.DropTable(
                name: "route_quality",
                schema: "routes");

            migrationBuilder.DropTable(
                name: "settings",
                schema: "routes");
        }
    }
}
