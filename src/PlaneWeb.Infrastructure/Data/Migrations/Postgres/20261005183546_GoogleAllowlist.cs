using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PlaneWeb.Infrastructure.Data.Migrations.Postgres
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    AddedBy = table.Column<string>(type: "text", nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
