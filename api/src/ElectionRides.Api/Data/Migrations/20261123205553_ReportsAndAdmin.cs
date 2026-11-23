using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectionRides.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReportsAndAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AssignedAt",
                table: "Requests",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedAt",
                table: "Requests");
        }
    }
}
