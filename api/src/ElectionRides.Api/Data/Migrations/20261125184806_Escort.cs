using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectionRides.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Escort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WithEscort",
                table: "Requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WithEscort",
                table: "Requests");
        }
    }
}
