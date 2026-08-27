using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace CityChecker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOsmBuildingFootprints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OsmBuildingFootprints",
                columns: table => new
                {
                    OsmBuildingFootprintId = table.Column<Guid>(type: "uuid", nullable: false),
                    CityId = table.Column<Guid>(type: "uuid", nullable: false),
                    DistrictId = table.Column<Guid>(type: "uuid", nullable: false),
                    OsmType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OsmId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Addr = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Geom = table.Column<MultiPolygon>(type: "geometry(MultiPolygon, 4326)", nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OsmBuildingFootprints", x => x.OsmBuildingFootprintId);
                    table.ForeignKey(
                        name: "FK_OsmBuildingFootprints_Cities_CityId",
                        column: x => x.CityId,
                        principalTable: "Cities",
                        principalColumn: "CityId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OsmBuildingFootprints_Districts_DistrictId",
                        column: x => x.DistrictId,
                        principalTable: "Districts",
                        principalColumn: "DistrictId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OsmBuildingFootprints_CityId",
                table: "OsmBuildingFootprints",
                column: "CityId");

            migrationBuilder.CreateIndex(
                name: "IX_OsmBuildingFootprints_DistrictId",
                table: "OsmBuildingFootprints",
                column: "DistrictId");

            migrationBuilder.CreateIndex(
                name: "IX_OsmBuildingFootprints_Geom",
                table: "OsmBuildingFootprints",
                column: "Geom")
                .Annotation("Npgsql:IndexMethod", "GIST");

            migrationBuilder.CreateIndex(
                name: "IX_OsmBuildingFootprints_OsmType_OsmId",
                table: "OsmBuildingFootprints",
                columns: new[] { "OsmType", "OsmId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OsmBuildingFootprints");
        }
    }
}
