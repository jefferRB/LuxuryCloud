using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddTilopayRecurringSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CodigoPlan",
                table: "Suscripciones",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FechaCancelacionUtc",
                table: "Suscripciones",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FechaFinGraciaUtc",
                table: "Suscripciones",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FechaProximoCobroUtc",
                table: "Suscripciones",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFuncionarios",
                table: "Suscripciones",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MonedaFacturacion",
                table: "Suscripciones",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PrecioMensual",
                table: "Suscripciones",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TilopayRecurringPlanId",
                table: "Suscripciones",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Codigo",
                table: "Planes",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LimiteMensajesMensual",
                table: "Planes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationToken",
                table: "PagosSuscripcion",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderSubscriberId",
                table: "PagosSuscripcion",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TilopayRecurringPlanId",
                table: "PagosSuscripcion",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Moneda",
                table: "EventosPago",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Monto",
                table: "EventosPago",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderSubscriberId",
                table: "EventosPago",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TilopayRecurringPlanId",
                table: "EventosPago",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantSubscriptionAddons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AddonCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Estado = table.Column<int>(type: "int", nullable: false),
                    TilopayRecurringPlanId = table.Column<int>(type: "int", nullable: true),
                    ProviderSubscriptionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ProviderTransactionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PrecioMensual = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    MonedaFacturacion = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    MonthlyMessageLimit = table.Column<int>(type: "int", nullable: false),
                    FechaInicio = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FechaFin = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FechaProximoCobroUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FechaFinGraciaUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FechaCancelacionUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSubscriptionAddons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantSubscriptionAddons_Planes_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Planes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantSubscriptionAddons_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
DECLARE @BasicPlanId uniqueidentifier = 'A1A61940-1080-46A7-AF9B-B74C767844DE';
DECLARE @ProPlanId uniqueidentifier = '4E36E22E-6F9F-41CA-9549-9BC548B7EC3A';
DECLARE @BusinessPlanId uniqueidentifier = '1087BDE5-404E-40DA-962E-D1E06D482361';
DECLARE @TestPlanId uniqueidentifier = 'B3D5C5F0-41AE-4A64-9D04-3A70B6D4F001';
DECLARE @Wa400PlanId uniqueidentifier = '5A4D17FE-818A-4D8C-84C3-31BF77AE0A40';
DECLARE @Wa800PlanId uniqueidentifier = '8758A61A-54EF-4C31-97D1-0D85E02A2F80';
DECLARE @Wa1200PlanId uniqueidentifier = 'AC17EA76-40A4-49F4-9750-C93DFEC3C0E0';

IF EXISTS (SELECT 1 FROM [Planes] WHERE [Id] = @BasicPlanId)
BEGIN
    UPDATE [Planes]
    SET [Codigo] = N'BASIC',
        [Nombre] = N'Basico',
        [Moneda] = N'CRC',
        [PrecioMensual] = CAST(8000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(0 AS bit),
        [MaxFuncionarios] = 1,
        [LimiteMensajesMensual] = NULL
    WHERE [Id] = @BasicPlanId;
END
ELSE IF EXISTS (SELECT 1 FROM [Planes] WHERE [Nombre] IN (N'Basico', N'Básico'))
BEGIN
    UPDATE [Planes]
    SET [Codigo] = N'BASIC',
        [Moneda] = N'CRC',
        [PrecioMensual] = CAST(8000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(0 AS bit),
        [MaxFuncionarios] = 1,
        [LimiteMensajesMensual] = NULL
    WHERE [Nombre] IN (N'Basico', N'Básico');
END
ELSE
BEGIN
    INSERT INTO [Planes] ([Id], [Codigo], [Nombre], [ProviderProductId], [ProviderPriceId], [Moneda], [PrecioMensual], [Activo], [EsPlanValidacion], [MaxFuncionarios], [LimiteMensajesMensual])
    VALUES (@BasicPlanId, N'BASIC', N'Basico', NULL, NULL, N'CRC', CAST(8000.00 AS decimal(18,2)), CAST(1 AS bit), CAST(0 AS bit), 1, NULL);
END;

IF EXISTS (SELECT 1 FROM [Planes] WHERE [Id] = @ProPlanId)
BEGIN
    UPDATE [Planes]
    SET [Codigo] = N'PRO',
        [Nombre] = N'Pro',
        [Moneda] = N'CRC',
        [PrecioMensual] = CAST(20000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(0 AS bit),
        [MaxFuncionarios] = 3,
        [LimiteMensajesMensual] = NULL
    WHERE [Id] = @ProPlanId;
END
ELSE IF EXISTS (SELECT 1 FROM [Planes] WHERE [Nombre] IN (N'Profesional', N'Pro'))
BEGIN
    UPDATE [Planes]
    SET [Codigo] = N'PRO',
        [Nombre] = N'Pro',
        [Moneda] = N'CRC',
        [PrecioMensual] = CAST(20000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(0 AS bit),
        [MaxFuncionarios] = 3,
        [LimiteMensajesMensual] = NULL
    WHERE [Nombre] IN (N'Profesional', N'Pro');
END
ELSE
BEGIN
    INSERT INTO [Planes] ([Id], [Codigo], [Nombre], [ProviderProductId], [ProviderPriceId], [Moneda], [PrecioMensual], [Activo], [EsPlanValidacion], [MaxFuncionarios], [LimiteMensajesMensual])
    VALUES (@ProPlanId, N'PRO', N'Pro', NULL, NULL, N'CRC', CAST(20000.00 AS decimal(18,2)), CAST(1 AS bit), CAST(0 AS bit), 3, NULL);
END;

IF EXISTS (SELECT 1 FROM [Planes] WHERE [Id] = @BusinessPlanId)
BEGIN
    UPDATE [Planes]
    SET [Codigo] = N'BUSINESS',
        [Nombre] = N'Business',
        [Moneda] = N'CRC',
        [PrecioMensual] = CAST(35000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(0 AS bit),
        [MaxFuncionarios] = 7,
        [LimiteMensajesMensual] = NULL
    WHERE [Id] = @BusinessPlanId;
END
ELSE IF EXISTS (SELECT 1 FROM [Planes] WHERE [Nombre] IN (N'Empresarial', N'Business'))
BEGIN
    UPDATE [Planes]
    SET [Codigo] = N'BUSINESS',
        [Nombre] = N'Business',
        [Moneda] = N'CRC',
        [PrecioMensual] = CAST(35000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(0 AS bit),
        [MaxFuncionarios] = 7,
        [LimiteMensajesMensual] = NULL
    WHERE [Nombre] IN (N'Empresarial', N'Business');
END
ELSE
BEGIN
    INSERT INTO [Planes] ([Id], [Codigo], [Nombre], [ProviderProductId], [ProviderPriceId], [Moneda], [PrecioMensual], [Activo], [EsPlanValidacion], [MaxFuncionarios], [LimiteMensajesMensual])
    VALUES (@BusinessPlanId, N'BUSINESS', N'Business', NULL, NULL, N'CRC', CAST(35000.00 AS decimal(18,2)), CAST(1 AS bit), CAST(0 AS bit), 7, NULL);
END;

IF EXISTS (SELECT 1 FROM [Planes] WHERE [Id] = @TestPlanId)
BEGIN
    UPDATE [Planes]
    SET [Codigo] = N'TEST_RECURRING',
        [Nombre] = N'Prueba Tilopay',
        [Moneda] = N'CRC',
        [PrecioMensual] = CAST(1000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(1 AS bit),
        [MaxFuncionarios] = 1,
        [LimiteMensajesMensual] = NULL
    WHERE [Id] = @TestPlanId;
END
ELSE IF EXISTS (SELECT 1 FROM [Planes] WHERE [Nombre] = N'Prueba Tilopay')
BEGIN
    UPDATE [Planes]
    SET [Codigo] = N'TEST_RECURRING',
        [Moneda] = N'CRC',
        [PrecioMensual] = CAST(1000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(1 AS bit),
        [MaxFuncionarios] = 1,
        [LimiteMensajesMensual] = NULL
    WHERE [Nombre] = N'Prueba Tilopay';
END
ELSE
BEGIN
    INSERT INTO [Planes] ([Id], [Codigo], [Nombre], [ProviderProductId], [ProviderPriceId], [Moneda], [PrecioMensual], [Activo], [EsPlanValidacion], [MaxFuncionarios], [LimiteMensajesMensual])
    VALUES (@TestPlanId, N'TEST_RECURRING', N'Prueba Tilopay', NULL, NULL, N'CRC', CAST(1000.00 AS decimal(18,2)), CAST(1 AS bit), CAST(1 AS bit), 1, NULL);
END;

IF EXISTS (SELECT 1 FROM [Planes] WHERE [Id] = @Wa400PlanId)
BEGIN
    UPDATE [Planes]
    SET [Codigo] = N'WA400',
        [Nombre] = N'WhatsApp 400',
        [Moneda] = N'CRC',
        [PrecioMensual] = CAST(6000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(0 AS bit),
        [MaxFuncionarios] = NULL,
        [LimiteMensajesMensual] = 400
    WHERE [Id] = @Wa400PlanId;
END
ELSE IF EXISTS (SELECT 1 FROM [Planes] WHERE [Codigo] = N'WA400' OR [Nombre] = N'WhatsApp 400')
BEGIN
    UPDATE [Planes]
    SET [Codigo] = N'WA400',
        [Nombre] = N'WhatsApp 400',
        [Moneda] = N'CRC',
        [PrecioMensual] = CAST(6000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(0 AS bit),
        [MaxFuncionarios] = NULL,
        [LimiteMensajesMensual] = 400
    WHERE [Codigo] = N'WA400' OR [Nombre] = N'WhatsApp 400';
END
ELSE
BEGIN
    INSERT INTO [Planes] ([Id], [Codigo], [Nombre], [ProviderProductId], [ProviderPriceId], [Moneda], [PrecioMensual], [Activo], [EsPlanValidacion], [MaxFuncionarios], [LimiteMensajesMensual])
    VALUES (@Wa400PlanId, N'WA400', N'WhatsApp 400', NULL, NULL, N'CRC', CAST(6000.00 AS decimal(18,2)), CAST(1 AS bit), CAST(0 AS bit), NULL, 400);
END;

IF EXISTS (SELECT 1 FROM [Planes] WHERE [Id] = @Wa800PlanId)
BEGIN
    UPDATE [Planes]
    SET [Codigo] = N'WA800',
        [Nombre] = N'WhatsApp 800',
        [Moneda] = N'CRC',
        [PrecioMensual] = CAST(12000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(0 AS bit),
        [MaxFuncionarios] = NULL,
        [LimiteMensajesMensual] = 800
    WHERE [Id] = @Wa800PlanId;
END
ELSE IF EXISTS (SELECT 1 FROM [Planes] WHERE [Codigo] = N'WA800' OR [Nombre] = N'WhatsApp 800')
BEGIN
    UPDATE [Planes]
    SET [Codigo] = N'WA800',
        [Nombre] = N'WhatsApp 800',
        [Moneda] = N'CRC',
        [PrecioMensual] = CAST(12000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(0 AS bit),
        [MaxFuncionarios] = NULL,
        [LimiteMensajesMensual] = 800
    WHERE [Codigo] = N'WA800' OR [Nombre] = N'WhatsApp 800';
END
ELSE
BEGIN
    INSERT INTO [Planes] ([Id], [Codigo], [Nombre], [ProviderProductId], [ProviderPriceId], [Moneda], [PrecioMensual], [Activo], [EsPlanValidacion], [MaxFuncionarios], [LimiteMensajesMensual])
    VALUES (@Wa800PlanId, N'WA800', N'WhatsApp 800', NULL, NULL, N'CRC', CAST(12000.00 AS decimal(18,2)), CAST(1 AS bit), CAST(0 AS bit), NULL, 800);
END;

IF EXISTS (SELECT 1 FROM [Planes] WHERE [Id] = @Wa1200PlanId)
BEGIN
    UPDATE [Planes]
    SET [Codigo] = N'WA1200',
        [Nombre] = N'WhatsApp 1200',
        [Moneda] = N'CRC',
        [PrecioMensual] = CAST(18000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(0 AS bit),
        [MaxFuncionarios] = NULL,
        [LimiteMensajesMensual] = 1200
    WHERE [Id] = @Wa1200PlanId;
END
ELSE IF EXISTS (SELECT 1 FROM [Planes] WHERE [Codigo] = N'WA1200' OR [Nombre] = N'WhatsApp 1200')
BEGIN
    UPDATE [Planes]
    SET [Codigo] = N'WA1200',
        [Nombre] = N'WhatsApp 1200',
        [Moneda] = N'CRC',
        [PrecioMensual] = CAST(18000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(0 AS bit),
        [MaxFuncionarios] = NULL,
        [LimiteMensajesMensual] = 1200
    WHERE [Codigo] = N'WA1200' OR [Nombre] = N'WhatsApp 1200';
END
ELSE
BEGIN
    INSERT INTO [Planes] ([Id], [Codigo], [Nombre], [ProviderProductId], [ProviderPriceId], [Moneda], [PrecioMensual], [Activo], [EsPlanValidacion], [MaxFuncionarios], [LimiteMensajesMensual])
    VALUES (@Wa1200PlanId, N'WA1200', N'WhatsApp 1200', NULL, NULL, N'CRC', CAST(18000.00 AS decimal(18,2)), CAST(1 AS bit), CAST(0 AS bit), NULL, 1200);
END;

UPDATE s
SET [CodigoPlan] = p.[Codigo],
    [PrecioMensual] = COALESCE(s.[PrecioMensual], p.[PrecioMensual]),
    [MonedaFacturacion] = COALESCE(s.[MonedaFacturacion], p.[Moneda]),
    [MaxFuncionarios] = COALESCE(s.[MaxFuncionarios], p.[MaxFuncionarios])
FROM [Suscripciones] AS s
INNER JOIN [Planes] AS p ON p.[Id] = s.[PlanId]
WHERE s.[CodigoPlan] IS NULL
   OR s.[PrecioMensual] IS NULL
   OR s.[MonedaFacturacion] IS NULL
   OR s.[MaxFuncionarios] IS NULL;
""");

            migrationBuilder.CreateIndex(
                name: "IX_Planes_Codigo",
                table: "Planes",
                column: "Codigo",
                unique: true,
                filter: "[Codigo] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PagosSuscripcion_Proveedor_ProviderSubscriberId",
                table: "PagosSuscripcion",
                columns: new[] { "Proveedor", "ProviderSubscriberId" },
                filter: "[ProviderSubscriberId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSubscriptionAddons_PlanId",
                table: "TenantSubscriptionAddons",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSubscriptionAddons_ProviderSubscriptionId",
                table: "TenantSubscriptionAddons",
                column: "ProviderSubscriptionId",
                unique: true,
                filter: "[ProviderSubscriptionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSubscriptionAddons_TenantId",
                table: "TenantSubscriptionAddons",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
DELETE FROM [Planes]
WHERE [Id] IN (
    '5A4D17FE-818A-4D8C-84C3-31BF77AE0A40',
    '8758A61A-54EF-4C31-97D1-0D85E02A2F80',
    'AC17EA76-40A4-49F4-9750-C93DFEC3C0E0'
)
AND NOT EXISTS (SELECT 1 FROM [Suscripciones] WHERE [PlanId] = [Planes].[Id])
AND NOT EXISTS (SELECT 1 FROM [PagosSuscripcion] WHERE [PlanId] = [Planes].[Id]);
""");

            migrationBuilder.DropTable(
                name: "TenantSubscriptionAddons");

            migrationBuilder.DropIndex(
                name: "IX_Planes_Codigo",
                table: "Planes");

            migrationBuilder.DropIndex(
                name: "IX_PagosSuscripcion_Proveedor_ProviderSubscriberId",
                table: "PagosSuscripcion");

            migrationBuilder.DropColumn(
                name: "CodigoPlan",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "FechaCancelacionUtc",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "FechaFinGraciaUtc",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "FechaProximoCobroUtc",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "MaxFuncionarios",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "MonedaFacturacion",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "PrecioMensual",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "TilopayRecurringPlanId",
                table: "Suscripciones");

            migrationBuilder.DropColumn(
                name: "Codigo",
                table: "Planes");

            migrationBuilder.DropColumn(
                name: "LimiteMensajesMensual",
                table: "Planes");

            migrationBuilder.DropColumn(
                name: "CorrelationToken",
                table: "PagosSuscripcion");

            migrationBuilder.DropColumn(
                name: "ProviderSubscriberId",
                table: "PagosSuscripcion");

            migrationBuilder.DropColumn(
                name: "TilopayRecurringPlanId",
                table: "PagosSuscripcion");

            migrationBuilder.DropColumn(
                name: "Moneda",
                table: "EventosPago");

            migrationBuilder.DropColumn(
                name: "Monto",
                table: "EventosPago");

            migrationBuilder.DropColumn(
                name: "ProviderSubscriberId",
                table: "EventosPago");

            migrationBuilder.DropColumn(
                name: "TilopayRecurringPlanId",
                table: "EventosPago");
        }
    }
}
