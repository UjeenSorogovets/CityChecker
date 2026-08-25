using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CityChecker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOtodomPinCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OtodomPinSets",
                columns: table => new
                {
                    PinSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    CityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Transaction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PriceMax = table.Column<int>(type: "integer", nullable: false),
                    AreaMin = table.Column<int>(type: "integer", nullable: false),
                    RoomsKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TotalMatched = table.Column<int>(type: "integer", nullable: false),
                    Listed = table.Column<int>(type: "integer", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OtodomPinSets", x => x.PinSetId);
                    table.ForeignKey(
                        name: "FK_OtodomPinSets_Cities_CityId",
                        column: x => x.CityId,
                        principalTable: "Cities",
                        principalColumn: "CityId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OtodomPins",
                columns: table => new
                {
                    PinId = table.Column<Guid>(type: "uuid", nullable: false),
                    PinSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<long>(type: "bigint", nullable: false),
                    Slug = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Lat = table.Column<double>(type: "double precision", nullable: false),
                    Lon = table.Column<double>(type: "double precision", nullable: false),
                    Price = table.Column<double>(type: "double precision", nullable: true),
                    AreaM2 = table.Column<double>(type: "double precision", nullable: true),
                    Rooms = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OtodomPins", x => x.PinId);
                    table.ForeignKey(
                        name: "FK_OtodomPins_OtodomPinSets_PinSetId",
                        column: x => x.PinSetId,
                        principalTable: "OtodomPinSets",
                        principalColumn: "PinSetId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OtodomPins_PinSetId_ExternalId",
                table: "OtodomPins",
                columns: new[] { "PinSetId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OtodomPins_PinSetId_Lat_Lon",
                table: "OtodomPins",
                columns: new[] { "PinSetId", "Lat", "Lon" });

            migrationBuilder.CreateIndex(
                name: "IX_OtodomPinSets_CityId_Transaction_PriceMax_AreaMin_RoomsKey",
                table: "OtodomPinSets",
                columns: new[] { "CityId", "Transaction", "PriceMax", "AreaMin", "RoomsKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OtodomPins");

            migrationBuilder.DropTable(
                name: "OtodomPinSets");
        }
    }
}
