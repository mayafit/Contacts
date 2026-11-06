using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContactsApi.Infrastructure.Atlas.Migrations
{
    /// <inheritdoc />
    public partial class AccessTokenExpiry : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// AD-50 rule 9: tokens that exist already expire 90 days from this migration and keep
        /// write access until then (scope "legacy" marks them for the notice in the token list).
        /// The column defaults only fill those rows and are dropped at the end; the app always
        /// sets both values.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                schema: "atlas",
                table: "access_tokens",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() + interval '90 days'");

            migrationBuilder.AddColumn<string>(
                name: "Scopes",
                schema: "atlas",
                table: "access_tokens",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "atlas write legacy");

            migrationBuilder.Sql("ALTER TABLE atlas.access_tokens ALTER COLUMN \"ExpiresAt\" DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE atlas.access_tokens ALTER COLUMN \"Scopes\" DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                schema: "atlas",
                table: "access_tokens");

            migrationBuilder.DropColumn(
                name: "Scopes",
                schema: "atlas",
                table: "access_tokens");
        }
    }
}
