using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CityChecker.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PointNotesReplaceDistrict : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Old NoteLevel.District == 1; delete whole-district notes (no content migration).
            migrationBuilder.Sql("""DELETE FROM "Notes" WHERE "Level" = 1;""");

            migrationBuilder.AddColumn<double>(
                name: "Lat",
                table: "Notes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Lon",
                table: "Notes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RadiusMeters",
                table: "Notes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notes_Lat_Lon",
                table: "Notes",
                columns: new[] { "Lat", "Lon" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notes_Lat_Lon",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "Lat",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "Lon",
                table: "Notes");

            migrationBuilder.DropColumn(
                name: "RadiusMeters",
                table: "Notes");
        }
    }
}
