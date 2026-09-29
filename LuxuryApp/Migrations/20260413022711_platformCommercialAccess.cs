using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class platformCommercialAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CommercialAccessMode",
                table: "Tenants",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CommercialNotes",
                table: "Tenants",
                type: "nvarchar(250)",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommercialUpdatedByUserId",
                table: "Tenants",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CommercialUpdatedUtc",
                table: "Tenants",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ForcedPlanId",
                table: "Tenants",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPlatformSuperAdmin",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PromotionalCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Codigo = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Activo = table.Column<bool>(type: "bit", nullable: false),
                    TipoBeneficio = table.Column<int>(type: "int", nullable: false),
                    DiasGratis = table.Column<int>(type: "int", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MaxUsos = table.Column<int>(type: "int", nullable: true),
                    UsosActuales = table.Column<int>(type: "int", nullable: false),
                    FechaExpiracionUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SoloPrimerRegistro = table.Column<bool>(type: "bit", nullable: false),
                    EmailObjetivo = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreadoPorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    NotasInternas = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    FechaCreacionUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FechaActualizacionUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromotionalCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PromotionalCodes_Planes_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Planes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantCommercialAccessGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Activo = table.Column<bool>(type: "bit", nullable: false),
                    RequiresBilling = table.Column<bool>(type: "bit", nullable: false),
                    FechaInicioUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FechaFinUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PromotionalCodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreadoPorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    NotasInternas = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantCommercialAccessGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantCommercialAccessGrants_Planes_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Planes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantCommercialAccessGrants_PromotionalCodes_PromotionalCodeId",
                        column: x => x.PromotionalCodeId,
                        principalTable: "PromotionalCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantCommercialAccessGrants_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PromotionalCodeRedemptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PromotionalCodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantCommercialAccessGrantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConsumidoPorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    EmailConsumidor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FechaConsumoUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromotionalCodeRedemptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PromotionalCodeRedemptions_PromotionalCodes_PromotionalCodeId",
                        column: x => x.PromotionalCodeId,
                        principalTable: "PromotionalCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PromotionalCodeRedemptions_TenantCommercialAccessGrants_TenantCommercialAccessGrantId",
                        column: x => x.TenantCommercialAccessGrantId,
                        principalTable: "TenantCommercialAccessGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_ForcedPlanId",
                table: "Tenants",
                column: "ForcedPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_PromotionalCodeRedemptions_PromotionalCodeId_TenantId",
                table: "PromotionalCodeRedemptions",
                columns: new[] { "PromotionalCodeId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PromotionalCodeRedemptions_TenantCommercialAccessGrantId",
                table: "PromotionalCodeRedemptions",
                column: "TenantCommercialAccessGrantId");

            migrationBuilder.CreateIndex(
                name: "IX_PromotionalCodeRedemptions_TenantId",
                table: "PromotionalCodeRedemptions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PromotionalCodes_Codigo",
                table: "PromotionalCodes",
                column: "Codigo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PromotionalCodes_PlanId",
                table: "PromotionalCodes",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantCommercialAccessGrants_PlanId",
                table: "TenantCommercialAccessGrants",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantCommercialAccessGrants_PromotionalCodeId",
                table: "TenantCommercialAccessGrants",
                column: "PromotionalCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantCommercialAccessGrants_TenantId",
                table: "TenantCommercialAccessGrants",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantCommercialAccessGrants_TenantId_Activo_FechaInicioUtc_FechaFinUtc",
                table: "TenantCommercialAccessGrants",
                columns: new[] { "TenantId", "Activo", "FechaInicioUtc", "FechaFinUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_Tenants_Planes_ForcedPlanId",
                table: "Tenants",
                column: "ForcedPlanId",
                principalTable: "Planes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tenants_Planes_ForcedPlanId",
                table: "Tenants");

            migrationBuilder.DropTable(
                name: "PromotionalCodeRedemptions");

            migrationBuilder.DropTable(
                name: "TenantCommercialAccessGrants");

            migrationBuilder.DropTable(
                name: "PromotionalCodes");

            migrationBuilder.DropIndex(
                name: "IX_Tenants_ForcedPlanId",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "CommercialAccessMode",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "CommercialNotes",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "CommercialUpdatedByUserId",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "CommercialUpdatedUtc",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ForcedPlanId",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "IsPlatformSuperAdmin",
                table: "AspNetUsers");
        }
    }
}
