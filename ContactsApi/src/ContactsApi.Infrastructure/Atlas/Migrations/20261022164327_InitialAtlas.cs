using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <inheritdoc />
    public partial class InitialAtlas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "atlas");

            migrationBuilder.CreateTable(
                name: "access_tokens",
                schema: "atlas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    Prefix = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_tokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "activities",
                schema: "atlas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: true),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DistanceM = table.Column<double>(type: "double precision", nullable: true),
                    DurationS = table.Column<double>(type: "double precision", nullable: true),
                    ElevationGainM = table.Column<double>(type: "double precision", nullable: true),
                    AvgHeartRate = table.Column<int>(type: "integer", nullable: true),
                    MaxHeartRate = table.Column<int>(type: "integer", nullable: true),
                    MinLat = table.Column<double>(type: "double precision", nullable: true),
                    MinLon = table.Column<double>(type: "double precision", nullable: true),
                    MaxLat = table.Column<double>(type: "double precision", nullable: true),
                    MaxLon = table.Column<double>(type: "double precision", nullable: true),
                    Polyline = table.Column<string>(type: "text", nullable: true),
                    PointCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "health_samples",
                schema: "atlas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Value = table.Column<double>(type: "double precision", nullable: false),
                    Unit = table.Column<string>(type: "text", nullable: true),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_health_samples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "imports",
                schema: "atlas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Activities = table.Column<int>(type: "integer", nullable: false),
                    Places = table.Column<int>(type: "integer", nullable: false),
                    Visits = table.Column<int>(type: "integer", nullable: false),
                    HealthSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_imports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "places",
                schema: "atlas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: true),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Address = table.Column<string>(type: "text", nullable: true),
                    Lat = table.Column<double>(type: "double precision", nullable: false),
                    Lon = table.Column<double>(type: "double precision", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: true),
                    GooglePlaceId = table.Column<string>(type: "text", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_places", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "track_points",
                schema: "atlas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Lat = table.Column<double>(type: "double precision", nullable: false),
                    Lon = table.Column<double>(type: "double precision", nullable: false),
                    Elevation = table.Column<double>(type: "double precision", nullable: true),
                    HeartRate = table.Column<int>(type: "integer", nullable: true),
                    Speed = table.Column<double>(type: "double precision", nullable: true),
                    Cadence = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_track_points", x => x.Id);
                    table.ForeignKey(
                        name: "FK_track_points_activities_ActivityId",
                        column: x => x.ActivityId,
                        principalSchema: "atlas",
                        principalTable: "activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "visits",
                schema: "atlas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExternalId = table.Column<string>(type: "text", nullable: true),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Lat = table.Column<double>(type: "double precision", nullable: false),
                    Lon = table.Column<double>(type: "double precision", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_visits_places_PlaceId",
                        column: x => x.PlaceId,
                        principalSchema: "atlas",
                        principalTable: "places",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_access_tokens_OwnerId",
                schema: "atlas",
                table: "access_tokens",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_access_tokens_TokenHash",
                schema: "atlas",
                table: "access_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_activities_ImportId",
                schema: "atlas",
                table: "activities",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_activities_OwnerId_Source_ExternalId",
                schema: "atlas",
                table: "activities",
                columns: new[] { "OwnerId", "Source", "ExternalId" },
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_activities_OwnerId_StartTime",
                schema: "atlas",
                table: "activities",
                columns: new[] { "OwnerId", "StartTime" });

            migrationBuilder.CreateIndex(
                name: "IX_health_samples_ImportId",
                schema: "atlas",
                table: "health_samples",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_health_samples_OwnerId_Type_StartTime_Source",
                schema: "atlas",
                table: "health_samples",
                columns: new[] { "OwnerId", "Type", "StartTime", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_imports_OwnerId_CreatedAt",
                schema: "atlas",
                table: "imports",
                columns: new[] { "OwnerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_places_ImportId",
                schema: "atlas",
                table: "places",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_places_OwnerId_Lat_Lon",
                schema: "atlas",
                table: "places",
                columns: new[] { "OwnerId", "Lat", "Lon" });

            migrationBuilder.CreateIndex(
                name: "IX_places_OwnerId_Source_ExternalId",
                schema: "atlas",
                table: "places",
                columns: new[] { "OwnerId", "Source", "ExternalId" },
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_track_points_ActivityId_Time",
                schema: "atlas",
                table: "track_points",
                columns: new[] { "ActivityId", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_visits_ImportId",
                schema: "atlas",
                table: "visits",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_visits_OwnerId_Source_ExternalId",
                schema: "atlas",
                table: "visits",
                columns: new[] { "OwnerId", "Source", "ExternalId" },
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_visits_OwnerId_StartTime",
                schema: "atlas",
                table: "visits",
                columns: new[] { "OwnerId", "StartTime" });

            migrationBuilder.CreateIndex(
                name: "IX_visits_PlaceId",
                schema: "atlas",
                table: "visits",
                column: "PlaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_tokens",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "health_samples",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "imports",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "track_points",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "visits",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "activities",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "places",
                schema: "atlas");
        }
    }
}
