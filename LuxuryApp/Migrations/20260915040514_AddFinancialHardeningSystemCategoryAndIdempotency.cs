using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialHardeningSystemCategoryAndIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IdempotencyKey",
                table: "LiquidacionesSemanales",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SystemCode",
                table: "Categorias",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            // ── Backfill de identidad estructural ────────────────────────────────────────────
            // DETERMINISTA y CONSERVADOR: solo etiqueta una categoría cuando NO hay ambigüedad,
            // es decir cuando el tenant tiene EXACTAMENTE UNA categoría con ese nombre. Si hubiera
            // dos (o ninguna), se deja NULL a propósito: el motor conserva para esas filas el
            // criterio histórico por nombre —comportamiento idéntico al de hoy, cero regresión— y
            // El script de verificación post-deploy las reporta para revisión manual.
            // Nunca se adivina a qué código pertenece una categoría ambigua.
            //
            // El RLS se apaga alrededor del UPDATE porque la sesión de despliegue no tiene TenantId
            // en SESSION_CONTEXT y el FILTER PREDICATE dejaría el backfill en cero filas.
            migrationBuilder.Sql("""
                DECLARE @policyOff bit = 0;

                IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'TenantSecurityPolicy' AND is_enabled = 1)
                BEGIN
                    ALTER SECURITY POLICY [dbo].[TenantSecurityPolicy] WITH (STATE = OFF);
                    SET @policyOff = 1;
                END

                BEGIN TRY
                    /*  El UPDATE va dentro de EXEC a propósito: SQL Server compila el batch COMPLETO
                        antes de ejecutarlo, y en el script idempotente de despliegue este bloque
                        viaja en el MISMO batch que el "ALTER TABLE Categorias ADD SystemCode". Sin
                        el EXEC, el compilador todavía no conoce la columna y rechaza el batch entero
                        con "Invalid column name 'SystemCode'" — es decir, el script no correría
                        nunca desde el estado real de producción.

                        Es exactamente la misma técnica que EF aplica por su cuenta a los CREATE
                        INDEX de esta migración; acá hace falta hacerlo a mano porque el SQL es
                        manual. Las comillas simples van duplicadas por estar anidadas.

                        La semántica NO cambia: mismas categorías, misma guarda COUNT(*) = 1, misma
                        transacción. El EXEC corre en el mismo ámbito transaccional, así que la
                        atomicidad se conserva, y un error dentro del SQL dinámico lo sigue
                        capturando el CATCH de abajo. */
                    EXEC (N'
                        /*  EXEC() arranca el batch dinámico con QUOTED_IDENTIFIER OFF, y la tabla
                            Categorias tiene un índice FILTRADO
                            (UX_Categorias_TenantId_Nombre_PagoFuncionarios). SQL Server exige
                            QUOTED_IDENTIFIER ON para cualquier DML sobre una tabla con índices
                            filtrados; sin esto el UPDATE falla con el error 1934. No cambia el
                            significado de nada: QUOTED_IDENTIFIER solo afecta a las comillas
                            DOBLES, y acá todos los literales usan comillas simples. */
                        SET QUOTED_IDENTIFIER ON;

                        ;WITH Mapa(Nombre, SystemCode) AS
                        (
                            SELECT * FROM (VALUES
                                (N''Pago Funcionarios'',              N''EmployeeSettlement''),
                                (N''Distribución a inversionistas'',  N''InvestorDistribution''),
                                (N''Costo laboral extraordinario'',   N''ExtraordinaryLaborCost'')
                            ) AS v(Nombre, SystemCode)
                        )
                        UPDATE c
                        SET c.SystemCode = m.SystemCode
                        FROM dbo.Categorias AS c
                        INNER JOIN Mapa AS m ON c.Nombre = m.Nombre
                        WHERE c.SystemCode IS NULL
                          AND (
                                SELECT COUNT(*)
                                FROM dbo.Categorias AS d
                                WHERE d.TenantId = c.TenantId
                                  AND d.Nombre = m.Nombre
                              ) = 1;');
                END TRY
                BEGIN CATCH
                    IF @policyOff = 1
                        ALTER SECURITY POLICY [dbo].[TenantSecurityPolicy] WITH (STATE = ON);
                    THROW;
                END CATCH

                IF @policyOff = 1
                    ALTER SECURITY POLICY [dbo].[TenantSecurityPolicy] WITH (STATE = ON);
                """);

            migrationBuilder.CreateIndex(
                name: "UX_LiquidacionesSemanales_TenantId_IdempotencyKey",
                table: "LiquidacionesSemanales",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Categorias_TenantId_SystemCode",
                table: "Categorias",
                columns: new[] { "TenantId", "SystemCode" },
                unique: true,
                filter: "[SystemCode] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_LiquidacionesSemanales_TenantId_IdempotencyKey",
                table: "LiquidacionesSemanales");

            migrationBuilder.DropIndex(
                name: "UX_Categorias_TenantId_SystemCode",
                table: "Categorias");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "LiquidacionesSemanales");

            migrationBuilder.DropColumn(
                name: "SystemCode",
                table: "Categorias");
        }
    }
}
