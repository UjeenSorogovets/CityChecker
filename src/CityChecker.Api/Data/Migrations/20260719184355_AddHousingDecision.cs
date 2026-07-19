using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CityChecker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHousingDecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DecisionProfiles",
                columns: table => new
                {
                    ProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WeightCommute = table.Column<int>(type: "integer", nullable: false),
                    WeightQuiet = table.Column<int>(type: "integer", nullable: false),
                    WeightPrice = table.Column<int>(type: "integer", nullable: false),
                    WeightGreen = table.Column<int>(type: "integer", nullable: false),
                    WeightComfort = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionProfiles", x => x.ProfileId);
                });

            migrationBuilder.CreateTable(
                name: "DistrictPicks",
                columns: table => new
                {
                    PickId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DistrictId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    VetoReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    QuietScore = table.Column<int>(type: "integer", nullable: true),
                    ParkCount = table.Column<int>(type: "integer", nullable: true),
                    ShopCount = table.Column<int>(type: "integer", nullable: true),
                    PharmacyCount = table.Column<int>(type: "integer", nullable: true),
                    SchoolCount = table.Column<int>(type: "integer", nullable: true),
                    TransitStopCount = table.Column<int>(type: "integer", nullable: true),
                    NearestHighwayKm = table.Column<double>(type: "double precision", nullable: true),
                    ReminderAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReminderNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RiskNotes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DistrictPicks", x => x.PickId);
                    table.ForeignKey(
                        name: "FK_DistrictPicks_Districts_DistrictId",
                        column: x => x.DistrictId,
                        principalTable: "Districts",
                        principalColumn: "DistrictId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DistrictVisits",
                columns: table => new
                {
                    VisitId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DistrictId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EveningFeel = table.Column<int>(type: "integer", nullable: true),
                    Daylight = table.Column<int>(type: "integer", nullable: true),
                    DogWalk = table.Column<int>(type: "integer", nullable: true),
                    SaturdayLife = table.Column<int>(type: "integer", nullable: true),
                    WinterFeel = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DistrictVisits", x => x.VisitId);
                    table.ForeignKey(
                        name: "FK_DistrictVisits_Districts_DistrictId",
                        column: x => x.DistrictId,
                        principalTable: "Districts",
                        principalColumn: "DistrictId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HousingOffers",
                columns: table => new
                {
                    OfferId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CityId = table.Column<Guid>(type: "uuid", nullable: true),
                    DistrictId = table.Column<Guid>(type: "uuid", nullable: true),
                    BuildingId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Lat = table.Column<double>(type: "double precision", nullable: false),
                    Lon = table.Column<double>(type: "double precision", nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: true),
                    Sqm = table.Column<double>(type: "double precision", nullable: true),
                    Floor = table.Column<int>(type: "integer", nullable: true),
                    Rooms = table.Column<int>(type: "integer", nullable: true),
                    YearBuilt = table.Column<int>(type: "integer", nullable: true),
                    RentOrMortgage = table.Column<decimal>(type: "numeric", nullable: true),
                    Media = table.Column<decimal>(type: "numeric", nullable: true),
                    Internet = table.Column<decimal>(type: "numeric", nullable: true),
                    ParkingFee = table.Column<decimal>(type: "numeric", nullable: true),
                    Czynsz = table.Column<decimal>(type: "numeric", nullable: true),
                    ScorePrice = table.Column<int>(type: "integer", nullable: true),
                    ScoreLayout = table.Column<int>(type: "integer", nullable: true),
                    ScoreLight = table.Column<int>(type: "integer", nullable: true),
                    ScoreCondition = table.Column<int>(type: "integer", nullable: true),
                    ScoreNeighbors = table.Column<int>(type: "integer", nullable: true),
                    ScoreHeating = table.Column<int>(type: "integer", nullable: true),
                    ScoreBalcony = table.Column<int>(type: "integer", nullable: true),
                    ScoreElevator = table.Column<int>(type: "integer", nullable: true),
                    ScoreParking = table.Column<int>(type: "integer", nullable: true),
                    ScoreCellar = table.Column<int>(type: "integer", nullable: true),
                    KillerFlaw = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PricePerSqm = table.Column<decimal>(type: "numeric", nullable: true),
                    RenovationBudget = table.Column<decimal>(type: "numeric", nullable: true),
                    HasKsiega = table.Column<bool>(type: "boolean", nullable: true),
                    HasSluzebnosc = table.Column<bool>(type: "boolean", nullable: true),
                    HasSpoldzielniaDebt = table.Column<bool>(type: "boolean", nullable: true),
                    Deposit = table.Column<decimal>(type: "numeric", nullable: true),
                    NoticeDays = table.Column<int>(type: "integer", nullable: true),
                    Furnished = table.Column<bool>(type: "boolean", nullable: true),
                    LandlordNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PhotoUrls = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    VoiceNoteUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsFinalist = table.Column<bool>(type: "boolean", nullable: false),
                    ReminderAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReminderNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HousingOffers", x => x.OfferId);
                });

            migrationBuilder.CreateTable(
                name: "MapAnchors",
                columns: table => new
                {
                    AnchorId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Lat = table.Column<double>(type: "double precision", nullable: false),
                    Lon = table.Column<double>(type: "double precision", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapAnchors", x => x.AnchorId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionProfiles_UserId",
                table: "DecisionProfiles",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DistrictPicks_DistrictId",
                table: "DistrictPicks",
                column: "DistrictId");

            migrationBuilder.CreateIndex(
                name: "IX_DistrictPicks_UserId_DistrictId",
                table: "DistrictPicks",
                columns: new[] { "UserId", "DistrictId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DistrictVisits_DistrictId",
                table: "DistrictVisits",
                column: "DistrictId");

            migrationBuilder.CreateIndex(
                name: "IX_DistrictVisits_UserId_DistrictId",
                table: "DistrictVisits",
                columns: new[] { "UserId", "DistrictId" });

            migrationBuilder.CreateIndex(
                name: "IX_HousingOffers_UserId",
                table: "HousingOffers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_HousingOffers_UserId_IsFinalist",
                table: "HousingOffers",
                columns: new[] { "UserId", "IsFinalist" });

            migrationBuilder.CreateIndex(
                name: "IX_MapAnchors_UserId",
                table: "MapAnchors",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DecisionProfiles");

            migrationBuilder.DropTable(
                name: "DistrictPicks");

            migrationBuilder.DropTable(
                name: "DistrictVisits");

            migrationBuilder.DropTable(
                name: "HousingOffers");

            migrationBuilder.DropTable(
                name: "MapAnchors");
        }
    }
}
