using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlaneWeb.Infrastructure.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class GoogleAllowlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GoogleAllowedUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    IsAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                    AddedBy = table.Column<string>(type: "TEXT", nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoogleAllowedUsers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GoogleAllowedUsers_Email",
                table: "GoogleAllowedUsers",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoogleAllowedUsers");
        }
    }
}
