using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddInvestorCutoffDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConfigureSqlServerSessionOptions(migrationBuilder);

            migrationBuilder.AddColumn<int>(
                name: "DiaCorte",
                table: "InvestorStatements",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "FechaCorte",
                table: "InvestorStatements",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<int>(
                name: "DiaCorte",
                table: "InvestorAgreements",
                type: "int",
                nullable: true);

            /*
             * DiaCorte acaba de ser agregado.
             * Crear el CHECK mediante SQL dinámico evita resolución anticipada
             * de la columna al generar/ejecutar el script de producción.
             */
            migrationBuilder.Sql(
                """
                EXEC sys.sp_executesql N'
                ALTER TABLE [dbo].[InvestorAgreements]
                WITH CHECK
                ADD CONSTRAINT [CK_InvestorAgreements_DiaCorte]
                CHECK
                (
                    [DiaCorte] IS NULL
                    OR ([DiaCorte] >= 1 AND [DiaCorte] <= 31)
                );

                ALTER TABLE [dbo].[InvestorAgreements]
                CHECK CONSTRAINT [CK_InvestorAgreements_DiaCorte];
                ';
                """);

            BackfillFechaCorte(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConfigureSqlServerSessionOptions(migrationBuilder);

            migrationBuilder.DropCheckConstraint(
                name: "CK_InvestorAgreements_DiaCorte",
                table: "InvestorAgreements");

            migrationBuilder.DropColumn(
                name: "DiaCorte",
                table: "InvestorStatements");

            migrationBuilder.DropColumn(
                name: "FechaCorte",
                table: "InvestorStatements");

            migrationBuilder.DropColumn(
                name: "DiaCorte",
                table: "InvestorAgreements");
        }

        /// <summary>
        /// Opciones de sesión compatibles con los objetos e índices existentes
        /// de la base de datos de producción.
        /// </summary>
        private static void ConfigureSqlServerSessionOptions(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                SET ANSI_NULLS ON;
                SET ANSI_PADDING ON;
                SET ANSI_WARNINGS ON;
                SET ARITHABORT ON;
                SET CONCAT_NULL_YIELDS_NULL ON;
                SET QUOTED_IDENTIFIER ON;
                SET NUMERIC_ROUNDABORT OFF;
                """);
        }

        /// <summary>
        /// Rellena FechaCorte de estados históricos usando PeriodoFin.
        ///
        /// DiaCorte se mantiene NULL para datos históricos porque NULL representa
        /// período de mes calendario.
        ///
        /// La política RLS específica de InvestorStatements se apaga únicamente
        /// durante el UPDATE y luego se devuelve a su estado original.
        /// </summary>
        private static void BackfillFechaCorte(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DECLARE @policySchema sysname;
                DECLARE @policyName sysname;
                DECLARE @qualifiedPolicy nvarchar(300);
                DECLARE @sql nvarchar(max);
                DECLARE @wasEnabled bit;

                /*
                 * Buscar específicamente la política que protege
                 * InvestorStatements y no simplemente la primera policy
                 * de toda la base que utilice fnTenantAccess.
                 */
                SELECT TOP (1)
                    @policySchema = SCHEMA_NAME(policy.schema_id),
                    @policyName = policy.name,
                    @wasEnabled = policy.is_enabled
                FROM sys.security_policies AS policy
                INNER JOIN sys.security_predicates AS predicate
                    ON predicate.object_id = policy.object_id
                WHERE predicate.target_object_id =
                          OBJECT_ID(N'[dbo].[InvestorStatements]', N'U')
                  AND predicate.predicate_definition LIKE N'%fnTenantAccess%'
                ORDER BY policy.name;

                IF @policyName IS NOT NULL
                BEGIN
                    SET @qualifiedPolicy =
                        QUOTENAME(@policySchema) + N'.' + QUOTENAME(@policyName);

                    IF @wasEnabled = 1
                    BEGIN
                        SET @sql =
                            N'ALTER SECURITY POLICY '
                            + @qualifiedPolicy
                            + N' WITH (STATE = OFF);';

                        EXEC sys.sp_executesql @sql;
                    END;
                END;

                BEGIN TRY

                    /*
                     * FechaCorte acaba de ser creada en esta migración.
                     * Se usa SQL dinámico para retrasar su resolución hasta
                     * después de que ALTER TABLE haya terminado.
                     */
                    SET @sql = N'
                    UPDATE [dbo].[InvestorStatements]
                    SET [FechaCorte] = [PeriodoFin]
                    WHERE [FechaCorte] < CONVERT(date, ''19000101'', 112);
                    ';

                    EXEC sys.sp_executesql @sql;

                    IF @policyName IS NOT NULL
                       AND @wasEnabled = 1
                    BEGIN
                        SET @sql =
                            N'ALTER SECURITY POLICY '
                            + @qualifiedPolicy
                            + N' WITH (STATE = ON);';

                        EXEC sys.sp_executesql @sql;
                    END;

                END TRY
                BEGIN CATCH

                    /*
                     * Si falla el backfill, intentar restaurar RLS antes
                     * de propagar nuevamente la excepción.
                     */
                    IF @policyName IS NOT NULL
                       AND @wasEnabled = 1
                    BEGIN
                        SET @sql =
                            N'ALTER SECURITY POLICY '
                            + @qualifiedPolicy
                            + N' WITH (STATE = ON);';

                        EXEC sys.sp_executesql @sql;
                    END;

                    THROW;

                END CATCH;
                """);
        }
    }
}