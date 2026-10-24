using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <inheritdoc />
    public partial class ActivityEfforts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "activity_efforts",
                schema: "atlas",
                columns: table => new
                {
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PointCount = table.Column<int>(type: "integer", nullable: false),
                    Efforts = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activity_efforts", x => x.ActivityId);
                    table.ForeignKey(
                        name: "FK_activity_efforts_activities_ActivityId",
                        column: x => x.ActivityId,
                        principalSchema: "atlas",
                        principalTable: "activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_activity_efforts_OwnerId_Type",
                schema: "atlas",
                table: "activity_efforts",
                columns: new[] { "OwnerId", "Type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity_efforts",
                schema: "atlas");
        }
    }
}
