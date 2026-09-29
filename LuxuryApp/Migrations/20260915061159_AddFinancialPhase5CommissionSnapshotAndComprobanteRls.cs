using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialPhase5CommissionSnapshotAndComprobanteRls : Migration
    {
        /// <summary>
        /// Comprobantes internos: llevan el desglose fiscal de cada cobro y hasta hoy no tenían
        /// ningún predicado de seguridad.
        ///
        /// <para>
        /// <b>Solo reciben BLOCK, NO FILTER.</b> La ruta pública <c>/comprobantes/{token}</c> es
        /// anónima por diseño: el cliente abre su comprobante sin login y por lo tanto sin TenantId
        /// en la sesión. Un FILTER PREDICATE actúa por debajo de EF —<c>IgnoreQueryFilters</c> no lo
        /// esquiva— y dejaría esa consulta en cero filas, rompiendo todos los comprobantes ya
        /// enviados por correo.
        /// </para>
        ///
        /// <para>
        /// Los BLOCK sí se pueden aplicar y son los que cierran el riesgo de ESCRITURA: nadie puede
        /// crear ni mover un comprobante hacia otro tenant. La lectura queda como estaba, protegida
        /// por el filtro global de EF en todos los caminos autenticados y por un token aleatorio no
        /// adivinable en el único camino anónimo. Ver el informe para cómo cerrar también la lectura.
        /// </para>
        /// </summary>
        private static readonly string[] TablasComprobante =
        {
            "ComprobantesCobro",
            "ComprobanteCobroLineas"
        };
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ComisionCalculadaSobreSnapshot",
                table: "Cobros",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ModalidadIvaColaboradorSnapshot",
                table: "Cobros",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PorcentajeProductoSnapshot",
                table: "Cobros",
                type: "decimal(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PorcentajeServicioSnapshot",
                table: "Cobros",
                type: "decimal(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TarifaIvaColaboradorSnapshot",
                table: "Cobros",
                type: "decimal(5,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TipoRelacionColaboradorSnapshot",
                table: "Cobros",
                type: "int",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Cobros_SnapshotRemuneracion",
                table: "Cobros",
                sql: "([PorcentajeServicioSnapshot] IS NULL AND [PorcentajeProductoSnapshot] IS NULL AND [ComisionCalculadaSobreSnapshot] IS NULL AND [TipoRelacionColaboradorSnapshot] IS NULL AND [ModalidadIvaColaboradorSnapshot] IS NULL AND [TarifaIvaColaboradorSnapshot] IS NULL) OR ([PorcentajeServicioSnapshot] IS NOT NULL AND [PorcentajeProductoSnapshot] IS NOT NULL AND [ComisionCalculadaSobreSnapshot] IS NOT NULL AND [TipoRelacionColaboradorSnapshot] IS NOT NULL AND [ModalidadIvaColaboradorSnapshot] IS NOT NULL AND [TarifaIvaColaboradorSnapshot] IS NOT NULL)");

            migrationBuilder.Sql(ConstruirSqlComprobantes(agregar: true));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ConstruirSqlComprobantes(agregar: false));

            migrationBuilder.DropCheckConstraint(
                name: "CK_Cobros_SnapshotRemuneracion",
                table: "Cobros");

            migrationBuilder.DropColumn(
                name: "ComisionCalculadaSobreSnapshot",
                table: "Cobros");

            migrationBuilder.DropColumn(
                name: "ModalidadIvaColaboradorSnapshot",
                table: "Cobros");

            migrationBuilder.DropColumn(
                name: "PorcentajeProductoSnapshot",
                table: "Cobros");

            migrationBuilder.DropColumn(
                name: "PorcentajeServicioSnapshot",
                table: "Cobros");

            migrationBuilder.DropColumn(
                name: "TarifaIvaColaboradorSnapshot",
                table: "Cobros");

            migrationBuilder.DropColumn(
                name: "TipoRelacionColaboradorSnapshot",
                table: "Cobros");
        }

        /// <summary>
        /// SQL que agrega (o quita) los BLOCK PREDICATE de comprobantes. Es <c>internal</c> para que
        /// los tests de RLS ejecuten EXACTAMENTE el mismo SQL que se despliega.
        ///
        /// <para>
        /// Mismo patrón que el resto del sistema: <c>fnTenantAccess(TenantId)</c>, la misma política
        /// <c>TenantSecurityPolicy</c>, sin funciones nuevas y sin apagarla en ningún momento.
        /// </para>
        /// </summary>
        internal static string ConstruirSqlComprobantes(bool agregar)
        {
            var valores = string.Join(
                ", ",
                System.Array.ConvertAll(TablasComprobante, tabla => $"(N'{tabla}')"));

            var cuerpo = agregar ? CuerpoAgregarBloqueo : CuerpoQuitarBloqueo;

            return $"""
                DECLARE @tablas TABLE (Nombre sysname NOT NULL);

                INSERT INTO @tablas (Nombre)
                VALUES
                    {valores};

                DECLARE @policySchema sysname;
                DECLARE @policyName sysname;
                DECLARE @qualifiedPolicy nvarchar(300);
                DECLARE @sql nvarchar(max);
                DECLARE @tabla sysname;
                DECLARE @targetObjectId int;

                /*  La política se descubre por una tabla que YA está protegida, igual que en las
                    migraciones anteriores: no se hardcodea el nombre. */
                SELECT TOP (1)
                    @policySchema = SCHEMA_NAME(policy.schema_id),
                    @policyName   = policy.name
                FROM sys.security_policies AS policy
                INNER JOIN sys.security_predicates AS predicate
                    ON predicate.object_id = policy.object_id
                WHERE predicate.target_object_id = OBJECT_ID(N'[dbo].[Cobros]', N'U')
                  AND predicate.predicate_definition LIKE N'%fnTenantAccess%'
                ORDER BY policy.name;

                /*  Sin RLS en la base no hay nada que extender, y la migración NO debe fallar por
                    eso. IF/ELSE y no RETURN: el script idempotente de EF mete este bloque dentro de
                    un batch más grande y un RETURN abortaría el registro en __EFMigrationsHistory. */
                IF @policyName IS NOT NULL AND OBJECT_ID(N'[dbo].[fnTenantAccess]') IS NOT NULL
                BEGIN
                    SET @qualifiedPolicy = QUOTENAME(@policySchema) + N'.' + QUOTENAME(@policyName);

                    DECLARE comprobantes_cursor CURSOR LOCAL FAST_FORWARD FOR
                        SELECT Nombre FROM @tablas;

                    OPEN comprobantes_cursor;
                    FETCH NEXT FROM comprobantes_cursor INTO @tabla;

                    WHILE @@FETCH_STATUS = 0
                    BEGIN
                        SET @targetObjectId = OBJECT_ID(N'[dbo].' + QUOTENAME(@tabla), N'U');

                        IF @targetObjectId IS NOT NULL
                        BEGIN
                {cuerpo}
                        END;

                        FETCH NEXT FROM comprobantes_cursor INTO @tabla;
                    END;

                    CLOSE comprobantes_cursor;
                    DEALLOCATE comprobantes_cursor;
                END;
                """;
        }

        private const string CuerpoAgregarBloqueo = """
                        /* BLOCK AFTER INSERT: nadie crea un comprobante a nombre de otro tenant. */
                        IF NOT EXISTS (
                            SELECT 1 FROM sys.security_predicates
                            WHERE object_id = OBJECT_ID(@qualifiedPolicy)
                              AND target_object_id = @targetObjectId
                              AND predicate_type = 1
                              AND operation = 1)
                        BEGIN
                            SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy
                                     + N' ADD BLOCK PREDICATE [dbo].[fnTenantAccess]([TenantId]) ON [dbo].'
                                     + QUOTENAME(@tabla) + N' AFTER INSERT;';
                            EXEC sys.sp_executesql @sql;
                        END;

                        /* BLOCK AFTER UPDATE: nadie mueve un comprobante hacia otro tenant. */
                        IF NOT EXISTS (
                            SELECT 1 FROM sys.security_predicates
                            WHERE object_id = OBJECT_ID(@qualifiedPolicy)
                              AND target_object_id = @targetObjectId
                              AND predicate_type = 1
                              AND operation = 2)
                        BEGIN
                            SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy
                                     + N' ADD BLOCK PREDICATE [dbo].[fnTenantAccess]([TenantId]) ON [dbo].'
                                     + QUOTENAME(@tabla) + N' AFTER UPDATE;';
                            EXEC sys.sp_executesql @sql;
                        END;
                """;

        private const string CuerpoQuitarBloqueo = """
                        IF EXISTS (
                            SELECT 1 FROM sys.security_predicates
                            WHERE object_id = OBJECT_ID(@qualifiedPolicy)
                              AND target_object_id = @targetObjectId
                              AND predicate_type = 1
                              AND operation = 2)
                        BEGIN
                            SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy
                                     + N' DROP BLOCK PREDICATE ON [dbo].' + QUOTENAME(@tabla) + N' AFTER UPDATE;';
                            EXEC sys.sp_executesql @sql;
                        END;

                        IF EXISTS (
                            SELECT 1 FROM sys.security_predicates
                            WHERE object_id = OBJECT_ID(@qualifiedPolicy)
                              AND target_object_id = @targetObjectId
                              AND predicate_type = 1
                              AND operation = 1)
                        BEGIN
                            SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy
                                     + N' DROP BLOCK PREDICATE ON [dbo].' + QUOTENAME(@tabla) + N' AFTER INSERT;';
                            EXEC sys.sp_executesql @sql;
                        END;
                """;
    }
}
