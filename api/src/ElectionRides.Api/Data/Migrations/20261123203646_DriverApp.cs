using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectionRides.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DriverApp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Trips_DriverId",
                table: "Trips");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AcceptedAt",
                table: "Trips",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeclineReason",
                table: "Trips",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AccessTokenExpires",
                table: "Drivers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccessTokenHash",
                table: "Drivers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LocationAt",
                table: "Drivers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trips_DriverId",
                table: "Trips",
                column: "DriverId",
                unique: true,
                filter: "\"Status\" IN (1, 3)");

            migrationBuilder.CreateIndex(
                name: "IX_Drivers_AccessTokenHash",
                table: "Drivers",
                column: "AccessTokenHash",
                unique: true,
                filter: "\"AccessTokenHash\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Trips_DriverId",
                table: "Trips");

            migrationBuilder.DropIndex(
                name: "IX_Drivers_AccessTokenHash",
                table: "Drivers");

            migrationBuilder.DropColumn(
                name: "AcceptedAt",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "DeclineReason",
                table: "Trips");

            migrationBuilder.DropColumn(
                name: "AccessTokenExpires",
                table: "Drivers");

            migrationBuilder.DropColumn(
                name: "AccessTokenHash",
                table: "Drivers");

            migrationBuilder.DropColumn(
                name: "LocationAt",
                table: "Drivers");

            migrationBuilder.CreateIndex(
                name: "IX_Trips_DriverId",
                table: "Trips",
                column: "DriverId",
                unique: true,
                filter: "\"Status\" = 1");
        }
    }
}
