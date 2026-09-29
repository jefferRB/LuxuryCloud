using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlatformAuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ActorEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    TargetUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    TargetUserEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditLogs_Action",
                table: "PlatformAuditLogs",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditLogs_ActorUserId",
                table: "PlatformAuditLogs",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditLogs_CreatedAtUtc",
                table: "PlatformAuditLogs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditLogs_EntityType_EntityId",
                table: "PlatformAuditLogs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAuditLogs_TenantId",
                table: "PlatformAuditLogs",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformAuditLogs");
        }
    }
}
