using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlaneWeb.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    Shape = table.Column<int>(type: "INTEGER", nullable: false),
                    CenterLat = table.Column<double>(type: "REAL", nullable: false),
                    CenterLon = table.Column<double>(type: "REAL", nullable: false),
                    RadiusNm = table.Column<double>(type: "REAL", nullable: false),
                    PolygonJson = table.Column<string>(type: "TEXT", nullable: false),
                    TrackedFlights = table.Column<string>(type: "TEXT", nullable: false),
                    MinAltitudeFt = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAltitudeFt = table.Column<int>(type: "INTEGER", nullable: false),
                    IncludeGround = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeHelicopters = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeLight = table.Column<bool>(type: "INTEGER", nullable: false),
                    RotateSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAreaFlights = table.Column<int>(type: "INTEGER", nullable: false),
                    Units = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Settings");
        }
    }
}
