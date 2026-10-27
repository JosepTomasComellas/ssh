using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebSshTerminal.Data.Migrations
{
    /// <inheritdoc />
    public partial class Terminal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SshIdentityId",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", nullable: true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    Result = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    Amount = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SshIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LinuxUser = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SshIdentities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TerminalSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", nullable: false),
                    SshIdentityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TmuxName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TerminalSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TimeUtc",
                table: "AuditEvents",
                column: "TimeUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SshIdentities_Name",
                table: "SshIdentities",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TerminalSessions_OwnerUserId_State",
                table: "TerminalSessions",
                columns: new[] { "OwnerUserId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "SshIdentities");

            migrationBuilder.DropTable(
                name: "TerminalSessions");

            migrationBuilder.DropColumn(
                name: "SshIdentityId",
                table: "AspNetUsers");
        }
    }
}
