using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlaneWeb.Infrastructure.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AutoPage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoPage",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoPage",
                table: "Settings");
        }
    }
}
