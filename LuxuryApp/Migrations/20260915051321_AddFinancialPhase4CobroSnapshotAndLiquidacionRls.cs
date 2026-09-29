using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LuxuryApp.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialPhase4CobroSnapshotAndLiquidacionRls : Migration
    {
        /// <summary>Las tres tablas de liquidaciones que quedaron fuera de la política RLS.</summary>
        private static readonly string[] TablasLiquidacion =
        {
            "LiquidacionesSemanales",
            "LiquidacionesSemanalesDetalle",
            "LiquidacionesSemanalesDistribucionMensual"
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Snapshot financiero del cobro ────────────────────────────────────────────────
            // Todas nullable: la migración se puede aplicar ANTES de arrancar el código nuevo sin
            // romper nada. Los cobros existentes quedan en NULL = LEGACY, y siguen leyéndose con
            // el catálogo actual igual que hasta hoy. NO hay backfill: no podemos demostrar qué
            // configuración regía cuando se registraron y no vamos a inventarla.
            migrationBuilder.AddColumn<bool>(
                name: "AplicaIvaSnapshot",
                table: "Cobros",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DetalleSnapshot",
                table: "Cobros",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PrecioIncluyeIvaSnapshot",
                table: "Cobros",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TarifaIvaSnapshot",
                table: "Cobros",
                type: "decimal(5,2)",
                nullable: true);

            // O LEGACY (los tres NULL) o historia completa. Un snapshot a medias sería peor que
            // ninguno: el motor creería que el cobro trae su propia fiscalidad y leería tarifa nula.
            migrationBuilder.AddCheckConstraint(
                name: "CK_Cobros_SnapshotFiscal",
                table: "Cobros",
                sql: "([AplicaIvaSnapshot] IS NULL AND [TarifaIvaSnapshot] IS NULL AND [PrecioIncluyeIvaSnapshot] IS NULL) OR ([AplicaIvaSnapshot] IS NOT NULL AND [TarifaIvaSnapshot] IS NOT NULL AND [PrecioIncluyeIvaSnapshot] IS NOT NULL)");

            AgregarLiquidacionesALaPoliticaRls(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            QuitarLiquidacionesDeLaPoliticaRls(migrationBuilder);

            migrationBuilder.DropCheckConstraint(
                name: "CK_Cobros_SnapshotFiscal",
                table: "Cobros");

            migrationBuilder.DropColumn(
                name: "AplicaIvaSnapshot",
                table: "Cobros");

            migrationBuilder.DropColumn(
                name: "DetalleSnapshot",
                table: "Cobros");

            migrationBuilder.DropColumn(
                name: "PrecioIncluyeIvaSnapshot",
                table: "Cobros");

            migrationBuilder.DropColumn(
                name: "TarifaIvaSnapshot",
                table: "Cobros");
        }

        /// <summary>
        /// Cierra el hueco de aislamiento detectado en la auditoría de la Fase 3: las tres tablas
        /// de liquidaciones tienen TenantId propio pero NINGÚN predicado RLS, mientras el resto de
        /// las tablas financieras (Cobros, Egresos, Categorias…) sí los tiene. Hasta ahora su
        /// aislamiento dependía solo del filtro global de EF, que es fail-open cuando no hay
        /// tenant resuelto.
        ///
        /// <para>
        /// Como ya tienen TenantId, se usa EXACTAMENTE el mismo patrón que el resto del sistema
        /// —<c>fnTenantAccess(TenantId)</c> como FILTER + BLOCK AFTER INSERT + BLOCK AFTER UPDATE—
        /// sin desnormalizar nada, sin tocar el modelo y sin inventar una segunda estrategia.
        /// </para>
        ///
        /// <para>
        /// <b>La política nunca se apaga.</b> SQL Server permite agregar predicados a una política
        /// habilitada, así que no existe una ventana en la que los tenants queden expuestos. Las
        /// migraciones anteriores la apagaban porque además corrían un backfill de datos; acá no
        /// hay backfill que correr.
        /// </para>
        ///
        /// <para>Es idempotente: si el predicado ya existe, no se vuelve a agregar.</para>
        /// </summary>
        private static void AgregarLiquidacionesALaPoliticaRls(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(ConstruirSql(agregar: true));

        /// <summary>
        /// Revierte SOLO los predicados que agregó esta migración; no toca el resto de la política
        /// ni la apaga. Quitar un predicado también se puede hacer con la política habilitada.
        /// </summary>
        private static void QuitarLiquidacionesDeLaPoliticaRls(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(ConstruirSql(agregar: false));

        /// <summary>
        /// SQL que agrega (o quita) los predicados. Es <c>internal</c> para que los tests de RLS
        /// ejecuten EXACTAMENTE el mismo SQL que se va a desplegar, y no una copia que podría
        /// divergir sin que nadie se entere.
        /// </summary>
        internal static string ConstruirSql(bool agregar)
        {
            var valores = string.Join(
                ", ",
                System.Array.ConvertAll(TablasLiquidacion, tabla => $"(N'{tabla}')"));

            // El cuerpo del cursor cambia según se agreguen o se quiten predicados; el descubrimiento
            // de la política es idéntico en los dos sentidos.
            var cuerpo = agregar ? CuerpoAgregar : CuerpoQuitar;

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
                    migraciones anteriores: así no se hardcodea el nombre y funciona en cualquier base. */
                SELECT TOP (1)
                    @policySchema = SCHEMA_NAME(policy.schema_id),
                    @policyName   = policy.name
                FROM sys.security_policies AS policy
                INNER JOIN sys.security_predicates AS predicate
                    ON predicate.object_id = policy.object_id
                WHERE predicate.target_object_id = OBJECT_ID(N'[dbo].[Cobros]', N'U')
                  AND predicate.predicate_definition LIKE N'%fnTenantAccess%'
                ORDER BY policy.name;

                /*  Sin RLS en la base (por ejemplo un entorno de desarrollo recién creado) no hay
                    nada que extender, y la migración NO debe fallar por eso. Se usa IF/ELSE y no
                    RETURN a propósito: el script idempotente de EF inserta este bloque dentro de un
                    batch más grande, y un RETURN abortaría también el registro en
                    __EFMigrationsHistory dejando la migración "aplicada pero no anotada". */
                IF @policyName IS NOT NULL AND OBJECT_ID(N'[dbo].[fnTenantAccess]') IS NOT NULL
                BEGIN
                    SET @qualifiedPolicy = QUOTENAME(@policySchema) + N'.' + QUOTENAME(@policyName);

                    DECLARE tablas_cursor CURSOR LOCAL FAST_FORWARD FOR
                        SELECT Nombre FROM @tablas;

                    OPEN tablas_cursor;
                    FETCH NEXT FROM tablas_cursor INTO @tabla;

                    WHILE @@FETCH_STATUS = 0
                    BEGIN
                        SET @targetObjectId = OBJECT_ID(N'[dbo].' + QUOTENAME(@tabla), N'U');

                        IF @targetObjectId IS NOT NULL
                        BEGIN
                {cuerpo}
                        END;

                        FETCH NEXT FROM tablas_cursor INTO @tabla;
                    END;

                    CLOSE tablas_cursor;
                    DEALLOCATE tablas_cursor;
                END;
                """;
        }

        private const string CuerpoAgregar = """
                        /* FILTER: nadie LEE filas de otro tenant. */
                        IF NOT EXISTS (
                            SELECT 1 FROM sys.security_predicates
                            WHERE object_id = OBJECT_ID(@qualifiedPolicy)
                              AND target_object_id = @targetObjectId
                              AND predicate_type = 0)
                        BEGIN
                            SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy
                                     + N' ADD FILTER PREDICATE [dbo].[fnTenantAccess]([TenantId]) ON [dbo].'
                                     + QUOTENAME(@tabla) + N';';
                            EXEC sys.sp_executesql @sql;
                        END;

                        /* BLOCK AFTER INSERT: nadie ESCRIBE una fila a nombre de otro tenant. */
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

                        /* BLOCK AFTER UPDATE: nadie MUEVE una fila hacia otro tenant. */
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

        private const string CuerpoQuitar = """
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

                        IF EXISTS (
                            SELECT 1 FROM sys.security_predicates
                            WHERE object_id = OBJECT_ID(@qualifiedPolicy)
                              AND target_object_id = @targetObjectId
                              AND predicate_type = 0)
                        BEGIN
                            SET @sql = N'ALTER SECURITY POLICY ' + @qualifiedPolicy
                                     + N' DROP FILTER PREDICATE ON [dbo].' + QUOTENAME(@tabla) + N';';
                            EXEC sys.sp_executesql @sql;
                        END;
                """;
    }
}
