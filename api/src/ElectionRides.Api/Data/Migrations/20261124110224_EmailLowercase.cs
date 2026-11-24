using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElectionRides.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmailLowercase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"Users\" SET \"Email\" = lower(\"Email\") WHERE \"Email\" <> lower(\"Email\");");
            migrationBuilder.AddCheckConstraint(
                name: "CK_User_EmailLower",
                table: "Users",
                sql: "\"Email\" = lower(\"Email\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_User_EmailLower",
                table: "Users");
        }
    }
}
