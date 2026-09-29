using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingWeeklyBusinessHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantBookingBusinessHours",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DiaSemana = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    OpenTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    CloseTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantBookingBusinessHours", x => x.Id);
                    table.CheckConstraint("CK_TenantBookingBusinessHours_DiaSemana", "[DiaSemana] >= 0 AND [DiaSemana] <= 6");
                    table.CheckConstraint("CK_TenantBookingBusinessHours_Horario", "[OpenTime] < [CloseTime]");
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantBookingBusinessHours_TenantId",
                table: "TenantBookingBusinessHours",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_TenantBookingBusinessHours_TenantId_DiaSemana",
                table: "TenantBookingBusinessHours",
                columns: new[] { "TenantId", "DiaSemana" },
                unique: true);

            // ── Backfill de la jornada existente ─────────────────────────────────────────────
            // Cada tenant que ya tenía configuración conserva EXACTAMENTE su horario: los días
            // encendidos en WorkingDaysMask quedan abiertos con su OpenTime/CloseTime actuales y
            // los apagados quedan cerrados (guardando el mismo rango, que la restricción CHECK
            // exige aunque el día esté cerrado).
            //
            // Determinista y repetible: el NOT EXISTS por (TenantId, DiaSemana) hace que volver a
            // ejecutarlo no inserte nada. No se borra ni se modifica ninguna columna antigua: si
            // hubiera que revertir el despliegue, TenantBookingSettings sigue siendo suficiente.
            migrationBuilder.Sql(@"
INSERT INTO [dbo].[TenantBookingBusinessHours]
    ([TenantId], [DiaSemana], [IsEnabled], [OpenTime], [CloseTime], [CreatedAtUtc], [UpdatedAtUtc])
SELECT
    s.[TenantId],
    d.[DiaSemana],
    CASE WHEN (s.[WorkingDaysMask] & d.[Bit]) <> 0 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END,
    -- Defensa ante datos heredados inconsistentes: un rango imposible caería en el CHECK.
    CASE WHEN s.[CloseTime] > s.[OpenTime] THEN s.[OpenTime]  ELSE CAST('08:00:00' AS time) END,
    CASE WHEN s.[CloseTime] > s.[OpenTime] THEN s.[CloseTime] ELSE CAST('18:00:00' AS time) END,
    SYSUTCDATETIME(),
    SYSUTCDATETIME()
FROM [dbo].[TenantBookingSettings] AS s
CROSS JOIN (VALUES (0, 1), (1, 2), (2, 4), (3, 8), (4, 16), (5, 32), (6, 64)) AS d([DiaSemana], [Bit])
WHERE NOT EXISTS (
    SELECT 1
    FROM [dbo].[TenantBookingBusinessHours] AS h
    WHERE h.[TenantId] = s.[TenantId] AND h.[DiaSemana] = d.[DiaSemana]);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantBookingBusinessHours");
        }
    }
}
