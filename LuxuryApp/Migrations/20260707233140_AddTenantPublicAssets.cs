using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantPublicAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantPublicAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantPublicPageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ServicioId = table.Column<int>(type: "int", nullable: true),
                    AssetType = table.Column<int>(type: "int", nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PublicUrl = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantPublicAssets", x => x.Id);

                    table.ForeignKey(
                        name: "FK_TenantPublicAssets_Servicios_ServicioId",
                        column: x => x.ServicioId,
                        principalTable: "Servicios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);

                    table.ForeignKey(
                        name: "FK_TenantPublicAssets_TenantPublicPages_TenantPublicPageId",
                        column: x => x.TenantPublicPageId,
                        principalTable: "TenantPublicPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);

                    table.ForeignKey(
                        name: "FK_TenantPublicAssets_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantPublicAssets_ServicioId",
                table: "TenantPublicAssets",
                column: "ServicioId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPublicAssets_TenantId",
                table: "TenantPublicAssets",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantPublicAssets_TenantId_AssetType",
                table: "TenantPublicAssets",
                columns: new[] { "TenantId", "AssetType" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantPublicAssets_TenantId_ServicioId_AssetType",
                table: "TenantPublicAssets",
                columns: new[] { "TenantId", "ServicioId", "AssetType" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantPublicAssets_TenantPublicPageId",
                table: "TenantPublicAssets",
                column: "TenantPublicPageId");

            migrationBuilder.CreateIndex(
                name: "UX_TenantPublicAssets_StorageKey",
                table: "TenantPublicAssets",
                column: "StorageKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantPublicAssets");
        }
    }
}