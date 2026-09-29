using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class addValidationPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EsPlanValidacion",
                table: "Planes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
DECLARE @ValidationPlanId uniqueidentifier = 'B3D5C5F0-41AE-4A64-9D04-3A70B6D4F001';
DECLARE @ValidationPlanName nvarchar(50) = N'Prueba Tilopay';

IF EXISTS (SELECT 1 FROM [Planes] WHERE [Id] = @ValidationPlanId)
BEGIN
    UPDATE [Planes]
    SET [Nombre] = @ValidationPlanName,
        [Moneda] = N'CRC',
        [PrecioMensual] = CAST(1000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(1 AS bit),
        [MaxFuncionarios] = 1
    WHERE [Id] = @ValidationPlanId;
END
ELSE IF EXISTS (SELECT 1 FROM [Planes] WHERE [Nombre] = @ValidationPlanName)
BEGIN
    UPDATE [Planes]
    SET [Moneda] = N'CRC',
        [PrecioMensual] = CAST(1000.00 AS decimal(18,2)),
        [Activo] = CAST(1 AS bit),
        [EsPlanValidacion] = CAST(1 AS bit),
        [MaxFuncionarios] = 1
    WHERE [Nombre] = @ValidationPlanName;
END
ELSE
BEGIN
    INSERT INTO [Planes] (
        [Id],
        [Nombre],
        [ProviderProductId],
        [ProviderPriceId],
        [Moneda],
        [PrecioMensual],
        [Activo],
        [EsPlanValidacion],
        [MaxFuncionarios]
    )
    VALUES (
        @ValidationPlanId,
        @ValidationPlanName,
        NULL,
        NULL,
        N'CRC',
        CAST(1000.00 AS decimal(18,2)),
        CAST(1 AS bit),
        CAST(1 AS bit),
        1
    );
END;
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
DECLARE @ValidationPlanId uniqueidentifier = 'B3D5C5F0-41AE-4A64-9D04-3A70B6D4F001';
DECLARE @ValidationPlanName nvarchar(50) = N'Prueba Tilopay';

UPDATE [Planes]
SET [Activo] = CAST(0 AS bit)
WHERE [Id] = @ValidationPlanId
   OR [Nombre] = @ValidationPlanName;

DELETE FROM [Planes]
WHERE [Id] = @ValidationPlanId
  AND NOT EXISTS (SELECT 1 FROM [Suscripciones] WHERE [PlanId] = @ValidationPlanId)
  AND NOT EXISTS (SELECT 1 FROM [PagosSuscripcion] WHERE [PlanId] = @ValidationPlanId);
""");

            migrationBuilder.DropColumn(
                name: "EsPlanValidacion",
                table: "Planes");
        }
    }
}
