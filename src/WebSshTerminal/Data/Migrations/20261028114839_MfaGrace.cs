using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebSshTerminal.Data.Migrations
{
    /// <inheritdoc />
    public partial class MfaGrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MfaGraceUntilUtc",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MfaGraceUntilUtc",
                table: "AspNetUsers");
        }
    }
}
