using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantNotifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ActionUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    EntityType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    EntityId = table.Column<int>(type: "int", nullable: true),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsRead = table.Column<bool>(type: "bit", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantNotifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantNotifications_TenantId",
                table: "TenantNotifications",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantNotifications_TenantId_IsRead_CreatedAtUtc",
                table: "TenantNotifications",
                columns: new[] { "TenantId", "IsRead", "CreatedAtUtc" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "UX_TenantNotifications_TenantId_Type_Entity",
                table: "TenantNotifications",
                columns: new[] { "TenantId", "Type", "EntityType", "EntityId" },
                unique: true,
                filter: "[EntityType] IS NOT NULL AND [EntityId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantNotifications");
        }
    }
}
