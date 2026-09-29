using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformWorkerHeartbeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformWorkerHeartbeats",
                columns: table => new
                {
                    WorkerName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastBeatUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastCycleSummary = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformWorkerHeartbeats", x => x.WorkerName);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformWorkerHeartbeats");
        }
    }
}
