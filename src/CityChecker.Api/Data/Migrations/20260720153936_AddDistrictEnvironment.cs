using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CityChecker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDistrictEnvironment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CityEnvironmentSources",
                columns: table => new
                {
                    CityId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourcesGeoJson = table.Column<string>(type: "text", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CityEnvironmentSources", x => x.CityId);
                    table.ForeignKey(
                        name: "FK_CityEnvironmentSources_Cities_CityId",
                        column: x => x.CityId,
                        principalTable: "Cities",
                        principalColumn: "CityId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DistrictEnvironments",
                columns: table => new
                {
                    DistrictId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvRiskOverall = table.Column<int>(type: "integer", nullable: false),
                    NearestLandfillKm = table.Column<double>(type: "double precision", nullable: true),
                    NearestRailKm = table.Column<double>(type: "double precision", nullable: true),
                    NearestAirportKm = table.Column<double>(type: "double precision", nullable: true),
                    NearestIndustrialKm = table.Column<double>(type: "double precision", nullable: true),
                    NearestHighwayKm = table.Column<double>(type: "double precision", nullable: true),
                    LandfillDownwind = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DistrictEnvironments", x => x.DistrictId);
                    table.ForeignKey(
                        name: "FK_DistrictEnvironments_Districts_DistrictId",
                        column: x => x.DistrictId,
                        principalTable: "Districts",
                        principalColumn: "DistrictId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DistrictEnvironments_ComputedAt",
                table: "DistrictEnvironments",
                column: "ComputedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CityEnvironmentSources");

            migrationBuilder.DropTable(
                name: "DistrictEnvironments");
        }
    }
}
