using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlaneWeb.Infrastructure.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class MapSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defaults hand-edited so existing settings rows get the intended map defaults.
            migrationBuilder.AddColumn<string>(
                name: "MapLayer",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "streets");

            migrationBuilder.AddColumn<bool>(
                name: "MapShowAll",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "TraceBackfill",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "TrailMinutes",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 15);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MapLayer",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MapShowAll",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "TraceBackfill",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "TrailMinutes",
                table: "Settings");
        }
    }
}
