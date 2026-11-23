using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ElectionRides.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Auth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedLogins",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedUntil",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Audit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserEmail = table.Column<string>(type: "text", nullable: true),
                    CityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Details = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Audit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoginChallenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Used = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginChallenges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PartySettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PartyName = table.Column<string>(type: "text", nullable: true),
                    PrivacyOfficerName = table.Column<string>(type: "text", nullable: true),
                    PrivacyOfficerEmail = table.Column<string>(type: "text", nullable: true),
                    LegalAdvisorEmail = table.Column<string>(type: "text", nullable: true),
                    BaseDeletionDays = table.Column<int>(type: "integer", nullable: true),
                    RequireEmailCode = table.Column<bool>(type: "boolean", nullable: false),
                    RememberDeviceHours = table.Column<int>(type: "integer", nullable: false),
                    IdleTimeoutMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxFailedLogins = table.Column<int>(type: "integer", nullable: false),
                    LockMinutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartySettings", x => x.Id);
                    table.CheckConstraint("CK_PartySettings_Single", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "PasswordTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Used = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TrustedDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrustedDevices", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "PartySettings",
                columns: new[] { "Id", "BaseDeletionDays", "IdleTimeoutMinutes", "LegalAdvisorEmail", "LockMinutes", "MaxFailedLogins", "PartyName", "PrivacyOfficerEmail", "PrivacyOfficerName", "RememberDeviceHours", "RequireEmailCode" },
                values: new object[] { 1, null, 30, null, 15, 5, null, null, null, 24, true });

            migrationBuilder.CreateIndex(
                name: "IX_Audit_At",
                table: "Audit",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_Audit_CityId",
                table: "Audit",
                column: "CityId");

            migrationBuilder.CreateIndex(
                name: "IX_LoginChallenges_UserId",
                table: "LoginChallenges",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PasswordTokens_TokenHash",
                table: "PasswordTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevices_TokenHash",
                table: "TrustedDevices",
                column: "TokenHash",
                unique: true);

            // The audit log is append-only: the database itself refuses UPDATE and DELETE (spec 3.2).
            migrationBuilder.Sql(@"
CREATE FUNCTION audit_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'The audit log cannot be changed or deleted';
END $$;
CREATE TRIGGER ""TR_Audit_AppendOnly"" BEFORE UPDATE OR DELETE ON ""Audit""
  FOR EACH ROW EXECUTE FUNCTION audit_append_only();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""TR_Audit_AppendOnly"" ON ""Audit""; DROP FUNCTION IF EXISTS audit_append_only();");

            migrationBuilder.DropTable(
                name: "Audit");

            migrationBuilder.DropTable(
                name: "LoginChallenges");

            migrationBuilder.DropTable(
                name: "PartySettings");

            migrationBuilder.DropTable(
                name: "PasswordTokens");

            migrationBuilder.DropTable(
                name: "TrustedDevices");

            migrationBuilder.DropColumn(
                name: "FailedLogins",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LockedUntil",
                table: "Users");
        }
    }
}
