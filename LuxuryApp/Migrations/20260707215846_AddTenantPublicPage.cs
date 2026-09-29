using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantPublicPage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantPublicPages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsPublished = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    HeroTitle = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    HeroSubtitle = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(1500)", maxLength: 1500, nullable: true),
                    LogoUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CoverImageUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    WhatsAppPhone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    GoogleMapsUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    InstagramUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    FacebookUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TikTokUrl = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ShowServices = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    ShowPrices = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    ShowTeam = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    ShowLocation = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    ShowWhatsAppButton = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    SeoTitle = table.Column<string>(type: "nvarchar(70)", maxLength: 70, nullable: true),
                    SeoDescription = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantPublicPages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantPublicPages_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_TenantPublicPages_TenantId",
                table: "TenantPublicPages",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantPublicPages");
        }
    }
}
