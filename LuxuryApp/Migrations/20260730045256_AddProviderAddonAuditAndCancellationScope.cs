using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderAddonAuditAndCancellationScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PreviousProviderCancelledAtUtc",
                table: "TenantSubscriptionAddons",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousProviderSubscriptionId",
                table: "TenantSubscriptionAddons",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderCancellationSubscriptionId",
                table: "TenantSubscriptionAddons",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProviderAddonAuditSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActiveAddonSubscriberCount = table.Column<int>(type: "int", nullable: false),
                    HasDoubleActive = table.Column<bool>(type: "bit", nullable: false),
                    IsInconclusive = table.Column<bool>(type: "bit", nullable: false),
                    ActiveRecurringPlanIds = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ActiveSubscriberIds = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    LocalProviderSubscriptionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderAddonAuditSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAddonAuditSnapshots_HasDoubleActive",
                table: "ProviderAddonAuditSnapshots",
                column: "HasDoubleActive");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderAddonAuditSnapshots_TenantId",
                table: "ProviderAddonAuditSnapshots",
                column: "TenantId",
                unique: true);

            // ── Backfill CONSERVADOR de ProviderCancellationSubscriptionId ────────────────────
            // Las filas viejas no dicen a QUÉ suscriptor se refiere ProviderCancellation=2 (=Cancelled).
            // Solo se rellena el caso inequívoco: una cancelación de RENOVACIÓN del suscriptor VIGENTE
            // siempre deja CancelAtPeriodEnd=1 (la activación por pago lo pone en 0). Todo lo demás se
            // deja NULL a propósito: NULL significa "no consta que el actual esté dado de baja", que es
            // el lado seguro para el dinero (la cascada volverá a intentar la baja, y esa baja es
            // idempotente: si ya estaba inactivo, TiloPay lo confirma y no se cobra de más).
            migrationBuilder.Sql(@"
EXEC(N'
UPDATE [TenantSubscriptionAddons]
SET [ProviderCancellationSubscriptionId] = [ProviderSubscriptionId]
WHERE [ProviderCancellation] = 2
  AND [CancelAtPeriodEnd] = 1
  AND [ProviderSubscriptionId] IS NOT NULL;
');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderAddonAuditSnapshots");

            migrationBuilder.DropColumn(
                name: "PreviousProviderCancelledAtUtc",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "PreviousProviderSubscriptionId",
                table: "TenantSubscriptionAddons");

            migrationBuilder.DropColumn(
                name: "ProviderCancellationSubscriptionId",
                table: "TenantSubscriptionAddons");
        }
    }
}
