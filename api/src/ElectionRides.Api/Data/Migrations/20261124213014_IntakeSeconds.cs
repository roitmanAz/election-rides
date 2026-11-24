using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectionRides.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class IntakeSeconds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IntakeSeconds",
                table: "Requests",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IntakeSeconds",
                table: "Requests");
        }
    }
}
