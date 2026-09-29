using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanChangeIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlanChangeIntents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromPlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FromPlanCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    FromWorkerCount = table.Column<int>(type: "int", nullable: true),
                    FromTilopayRecurringPlanId = table.Column<int>(type: "int", nullable: true),
                    FromProviderSubscriptionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ToPlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToPlanCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ToWorkerCount = table.Column<int>(type: "int", nullable: false),
                    ToBillingCycle = table.Column<int>(type: "int", nullable: false),
                    ToTilopayRecurringPlanId = table.Column<int>(type: "int", nullable: false),
                    Estado = table.Column<int>(type: "int", nullable: false),
                    OldProviderCancellation = table.Column<int>(type: "int", nullable: false),
                    PagoSuscripcionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NewProviderSubscriptionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AppliedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanChangeIntents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlanChangeIntents_TenantId_Estado",
                table: "PlanChangeIntents",
                columns: new[] { "TenantId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_PlanChangeIntents_TenantId_OpenPending",
                table: "PlanChangeIntents",
                column: "TenantId",
                unique: true,
                filter: "[Estado] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlanChangeIntents");
        }
    }
}
