using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectionRides.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class StreetOfficialName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OfficialName",
                table: "Streets",
                type: "text",
                nullable: true);

            // Streets imported before this change: their current name is the official one.
            migrationBuilder.Sql(@"UPDATE ""Streets"" SET ""OfficialName"" = ""Name"" WHERE ""OfficialCode"" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OfficialName",
                table: "Streets");
        }
    }
}
