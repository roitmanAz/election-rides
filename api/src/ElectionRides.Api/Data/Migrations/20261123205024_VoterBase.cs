using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ElectionRides.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class VoterBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeletionCertificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    CityId = table.Column<Guid>(type: "uuid", nullable: false),
                    CityName = table.Column<string>(type: "text", nullable: false),
                    OperationText = table.Column<string>(type: "text", nullable: true),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    VoterCount = table.Column<int>(type: "integer", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedBy = table.Column<string>(type: "text", nullable: false),
                    WhatWasDeleted = table.Column<string>(type: "text", nullable: false),
                    SentTo = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeletionCertificates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VoterBases",
                columns: table => new
                {
                    CityId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    VoterCount = table.Column<int>(type: "integer", nullable: false),
                    LoadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LoadedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoterBases", x => x.CityId);
                });

            migrationBuilder.CreateTable(
                name: "Voters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CityId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdNumber = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    FirstName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    LastName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    BallotBox = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: true),
                    Age = table.Column<int>(type: "integer", nullable: true),
                    Street = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    BuildingNumber = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Voters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VoterPhones",
                columns: table => new
                {
                    VoterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Phone = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoterPhones", x => new { x.VoterId, x.Phone });
                    table.ForeignKey(
                        name: "FK_VoterPhones_Voters_VoterId",
                        column: x => x.VoterId,
                        principalTable: "Voters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeletionCertificates_CityId",
                table: "DeletionCertificates",
                column: "CityId");

            migrationBuilder.CreateIndex(
                name: "IX_DeletionCertificates_Number",
                table: "DeletionCertificates",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VoterPhones_Phone",
                table: "VoterPhones",
                column: "Phone");

            migrationBuilder.CreateIndex(
                name: "IX_Voters_CityId_IdNumber",
                table: "Voters",
                columns: new[] { "CityId", "IdNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeletionCertificates");

            migrationBuilder.DropTable(
                name: "VoterBases");

            migrationBuilder.DropTable(
                name: "VoterPhones");

            migrationBuilder.DropTable(
                name: "Voters");
        }
    }
}
