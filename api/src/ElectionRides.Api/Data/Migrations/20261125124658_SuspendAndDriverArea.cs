using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace ElectionRides.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SuspendAndDriverArea : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SuspendedAt",
                table: "Requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AreaBuilding",
                table: "Drivers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Point>(
                name: "AreaLocation",
                table: "Drivers",
                type: "geometry",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AreaStreetId",
                table: "Drivers",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SuspendedAt",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "AreaBuilding",
                table: "Drivers");

            migrationBuilder.DropColumn(
                name: "AreaLocation",
                table: "Drivers");

            migrationBuilder.DropColumn(
                name: "AreaStreetId",
                table: "Drivers");
        }
    }
}
