using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <inheritdoc />
    public partial class AtlasFusion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClusterId",
                schema: "atlas",
                table: "activities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TypeByUser",
                schema: "atlas",
                table: "activities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "activity_fusion",
                schema: "atlas",
                columns: table => new
                {
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Name = table.Column<string>(type: "text", nullable: true),
                    StartTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DistanceM = table.Column<double>(type: "double precision", nullable: true),
                    ElevationGainM = table.Column<double>(type: "double precision", nullable: true),
                    AvgHeartRate = table.Column<int>(type: "integer", nullable: true),
                    MaxHeartRate = table.Column<int>(type: "integer", nullable: true),
                    Sources = table.Column<string>(type: "text", nullable: false),
                    MemberCount = table.Column<int>(type: "integer", nullable: false),
                    ConflictCount = table.Column<int>(type: "integer", nullable: false),
                    FieldSources = table.Column<string>(type: "text", nullable: false),
                    Conflicts = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activity_fusion", x => x.ActivityId);
                    table.ForeignKey(
                        name: "FK_activity_fusion_activities_ActivityId",
                        column: x => x.ActivityId,
                        principalSchema: "atlas",
                        principalTable: "activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "fusion_decisions",
                schema: "atlas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActivityA = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityB = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fusion_decisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "source_connections",
                schema: "atlas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Account = table.Column<string>(type: "text", nullable: true),
                    SecretProtected = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    ItemsTotal = table.Column<int>(type: "integer", nullable: false),
                    ItemsDone = table.Column<int>(type: "integer", nullable: false),
                    ItemsImported = table.Column<int>(type: "integer", nullable: false),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_connections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_activities_ClusterId",
                schema: "atlas",
                table: "activities",
                column: "ClusterId");

            migrationBuilder.CreateIndex(
                name: "IX_activity_fusion_OwnerId",
                schema: "atlas",
                table: "activity_fusion",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_fusion_decisions_OwnerId_ActivityA_ActivityB",
                schema: "atlas",
                table: "fusion_decisions",
                columns: new[] { "OwnerId", "ActivityA", "ActivityB" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_connections_OwnerId_Provider",
                schema: "atlas",
                table: "source_connections",
                columns: new[] { "OwnerId", "Provider" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity_fusion",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "fusion_decisions",
                schema: "atlas");

            migrationBuilder.DropTable(
                name: "source_connections",
                schema: "atlas");

            migrationBuilder.DropIndex(
                name: "IX_activities_ClusterId",
                schema: "atlas",
                table: "activities");

            migrationBuilder.DropColumn(
                name: "ClusterId",
                schema: "atlas",
                table: "activities");

            migrationBuilder.DropColumn(
                name: "TypeByUser",
                schema: "atlas",
                table: "activities");
        }
    }
}
