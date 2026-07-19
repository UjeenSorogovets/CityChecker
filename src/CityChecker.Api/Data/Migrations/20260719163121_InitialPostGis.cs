using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CityChecker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostGis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "Cities",
                columns: table => new
                {
                    CityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Voivodeship = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CenterLat = table.Column<double>(type: "double precision", nullable: false),
                    CenterLon = table.Column<double>(type: "double precision", nullable: false),
                    OfficialCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cities", x => x.CityId);
                });

            migrationBuilder.CreateTable(
                name: "districts_import_raw",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Osiedla = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PunktyGraniczne = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ExtraJson = table.Column<string>(type: "text", nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_districts_import_raw", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Districts",
                columns: table => new
                {
                    DistrictId = table.Column<Guid>(type: "uuid", nullable: false),
                    CityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OfficialCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SourceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Geom = table.Column<MultiPolygon>(type: "geometry(MultiPolygon, 4326)", nullable: false),
                    AreaKm2 = table.Column<double>(type: "double precision", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Districts", x => x.DistrictId);
                    table.ForeignKey(
                        name: "FK_Districts_Cities_CityId",
                        column: x => x.CityId,
                        principalTable: "Cities",
                        principalColumn: "CityId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Buildings",
                columns: table => new
                {
                    BuildingId = table.Column<Guid>(type: "uuid", nullable: false),
                    CityId = table.Column<Guid>(type: "uuid", nullable: false),
                    DistrictId = table.Column<Guid>(type: "uuid", nullable: true),
                    AddressLine = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Lat = table.Column<double>(type: "double precision", nullable: false),
                    Lon = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Buildings", x => x.BuildingId);
                    table.ForeignKey(
                        name: "FK_Buildings_Cities_CityId",
                        column: x => x.CityId,
                        principalTable: "Cities",
                        principalColumn: "CityId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Buildings_Districts_DistrictId",
                        column: x => x.DistrictId,
                        principalTable: "Districts",
                        principalColumn: "DistrictId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Notes",
                columns: table => new
                {
                    NoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorGoogleId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    TargetCityId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetDistrictId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetBuildingId = table.Column<Guid>(type: "uuid", nullable: true),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ScoreOverall = table.Column<int>(type: "integer", nullable: false),
                    ScoreNature = table.Column<int>(type: "integer", nullable: true),
                    ScoreShops = table.Column<int>(type: "integer", nullable: true),
                    ScoreTransport = table.Column<int>(type: "integer", nullable: true),
                    ScoreSafety = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notes", x => x.NoteId);
                    table.ForeignKey(
                        name: "FK_Notes_Buildings_TargetBuildingId",
                        column: x => x.TargetBuildingId,
                        principalTable: "Buildings",
                        principalColumn: "BuildingId",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Notes_Cities_TargetCityId",
                        column: x => x.TargetCityId,
                        principalTable: "Cities",
                        principalColumn: "CityId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Notes_Districts_TargetDistrictId",
                        column: x => x.TargetDistrictId,
                        principalTable: "Districts",
                        principalColumn: "DistrictId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Buildings_CityId_AddressLine",
                table: "Buildings",
                columns: new[] { "CityId", "AddressLine" });

            migrationBuilder.CreateIndex(
                name: "IX_Buildings_DistrictId",
                table: "Buildings",
                column: "DistrictId");

            migrationBuilder.CreateIndex(
                name: "IX_Buildings_Lat_Lon",
                table: "Buildings",
                columns: new[] { "Lat", "Lon" });

            migrationBuilder.CreateIndex(
                name: "IX_Districts_CityId_Name",
                table: "Districts",
                columns: new[] { "CityId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Districts_Geom",
                table: "Districts",
                column: "Geom")
                .Annotation("Npgsql:IndexMethod", "GIST");

            migrationBuilder.CreateIndex(
                name: "IX_Notes_AuthorGoogleId",
                table: "Notes",
                column: "AuthorGoogleId");

            migrationBuilder.CreateIndex(
                name: "IX_Notes_TargetBuildingId",
                table: "Notes",
                column: "TargetBuildingId");

            migrationBuilder.CreateIndex(
                name: "IX_Notes_TargetCityId_Level",
                table: "Notes",
                columns: new[] { "TargetCityId", "Level" });

            migrationBuilder.CreateIndex(
                name: "IX_Notes_TargetDistrictId",
                table: "Notes",
                column: "TargetDistrictId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "districts_import_raw");

            migrationBuilder.DropTable(
                name: "Notes");

            migrationBuilder.DropTable(
                name: "Buildings");

            migrationBuilder.DropTable(
                name: "Districts");

            migrationBuilder.DropTable(
                name: "Cities");
        }
    }
}
