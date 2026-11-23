using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace ElectionRides.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Intake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AddressLocations",
                columns: table => new
                {
                    StreetId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildingNumber = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Location = table.Column<Point>(type: "geography (point, 4326)", nullable: true),
                    Approximate = table.Column<bool>(type: "boolean", nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AddressLocations", x => new { x.StreetId, x.BuildingNumber });
                });

            migrationBuilder.CreateTable(
                name: "People",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CityId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdNumber = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    FullName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Gender = table.Column<int>(type: "integer", nullable: false),
                    LastClusterId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CityId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    CallerPhone = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TakenById = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StreetId = table.Column<Guid>(type: "uuid", nullable: false),
                    BuildingNumber = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Entrance = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DriverNotes = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Location = table.Column<Point>(type: "geography (point, 4326)", nullable: true),
                    ApproximateLocation = table.Column<bool>(type: "boolean", nullable: false),
                    CommunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    NeedsWheelchair = table.Column<bool>(type: "boolean", nullable: false),
                    NeedsWalker = table.Column<bool>(type: "boolean", nullable: false),
                    NeedsHelp = table.Column<bool>(type: "boolean", nullable: false),
                    TimeMode = table.Column<int>(type: "integer", nullable: false),
                    WindowFrom = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    WindowTo = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    UrgencyReason = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ReturnMode = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CancelReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Requests_Streets_StreetId",
                        column: x => x.StreetId,
                        principalTable: "Streets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PersonPhones",
                columns: table => new
                {
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Phone = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonPhones", x => new { x.PersonId, x.Phone });
                    table.ForeignKey(
                        name: "FK_PersonPhones_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Passengers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    BallotBox = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    PendingVerification = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Passengers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Passengers_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Passengers_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Passengers_PersonId",
                table: "Passengers",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Passengers_RequestId",
                table: "Passengers",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_People_CityId_IdNumber",
                table: "People",
                columns: new[] { "CityId", "IdNumber" },
                unique: true,
                filter: "\"IdNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PersonPhones_Phone",
                table: "PersonPhones",
                column: "Phone");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_CallerPhone",
                table: "Requests",
                column: "CallerPhone");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_CityId_OperationId_Number",
                table: "Requests",
                columns: new[] { "CityId", "OperationId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Requests_CityId_Status",
                table: "Requests",
                columns: new[] { "CityId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Requests_StreetId",
                table: "Requests",
                column: "StreetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AddressLocations");

            migrationBuilder.DropTable(
                name: "Passengers");

            migrationBuilder.DropTable(
                name: "PersonPhones");

            migrationBuilder.DropTable(
                name: "Requests");

            migrationBuilder.DropTable(
                name: "People");
        }
    }
}
