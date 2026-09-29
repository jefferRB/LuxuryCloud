using LuxuryApp.Migrations;
using Microsoft.Data.SqlClient;

namespace LuxuryApp.Tests.Fiscal
{
    [CollectionDefinition(nameof(LiquidacionesRlsDatabase))]
    public sealed class LiquidacionesRlsCollection : ICollectionFixture<LiquidacionesRlsDatabase>
    {
    }

    /// <summary>
    /// Base temporal en SQL Server local que reproduce el ESTADO PREVIO a la Fase 4 y después
    /// aplica el SQL real de la migración.
    ///
    /// <para>
    /// El esquema es mínimo a propósito: solo <c>Cobros</c> (la tabla por la que la migración
    /// descubre la política) y las tres tablas de liquidaciones, cada una con su <c>TenantId</c>.
    /// Eso es exactamente el contrato del que depende la migración; montar las ~100 tablas del
    /// modelo completo no probaría nada adicional y volvería los tests lentos y frágiles.
    /// </para>
    ///
    /// <para>
    /// Punto clave: la política se crea con <c>STATE = ON</c> y NUNCA se apaga. Si SQL Server
    /// exigiera apagarla para agregar predicados, estos tests fallarían al aplicar la migración —
    /// que es justamente la garantía que promete la Fase 4 (§8: sin ventana de exposición).
    /// </para>
    ///
    /// <para>Requiere SQL Server: la instancia local con autenticación integrada, o la indicada en
    /// <c>LUXURYCLOUD_TEST_SQLSERVER</c> (CI usa un contenedor efímero).</para>
    /// </summary>
    public sealed class LiquidacionesRlsDatabase : IAsyncLifetime
    {
        public static readonly Guid TenantA = new("11111111-1111-1111-1111-111111111111");
        public static readonly Guid TenantB = new("22222222-2222-2222-2222-222222222222");

        private static string MasterConnection => Support.SqlServerTestConnection.Master;

        private readonly string _nombreBase = $"LuxuryAppRls_{Guid.NewGuid():N}";

        private string ConnectionString => Support.SqlServerTestConnection.ForDatabase(_nombreBase);

        public async Task InitializeAsync()
        {
            await EjecutarEnMasterAsync($"CREATE DATABASE [{_nombreBase}];");

            await EjecutarAsync($"""
                CREATE TABLE dbo.Cobros (
                    IdCobro int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    TenantId uniqueidentifier NOT NULL,
                    Monto decimal(18,2) NOT NULL);

                CREATE TABLE dbo.LiquidacionesSemanales (
                    Id int NOT NULL PRIMARY KEY,
                    TenantId uniqueidentifier NOT NULL,
                    MontoTotal decimal(18,2) NOT NULL);

                CREATE TABLE dbo.LiquidacionesSemanalesDetalle (
                    Id int NOT NULL PRIMARY KEY,
                    TenantId uniqueidentifier NOT NULL,
                    LiquidacionSemanalId int NOT NULL);

                CREATE TABLE dbo.LiquidacionesSemanalesDistribucionMensual (
                    Id int NOT NULL PRIMARY KEY,
                    TenantId uniqueidentifier NOT NULL,
                    LiquidacionSemanalId int NOT NULL);

                CREATE TABLE dbo.ComprobantesCobro (
                    Id int NOT NULL PRIMARY KEY,
                    TenantId uniqueidentifier NOT NULL,
                    TokenPublico nvarchar(64) NOT NULL,
                    Total decimal(18,2) NOT NULL);

                CREATE TABLE dbo.ComprobanteCobroLineas (
                    Id int NOT NULL PRIMARY KEY,
                    TenantId uniqueidentifier NOT NULL,
                    ComprobanteCobroId int NOT NULL);
                """);

            // Función y política tal como existen hoy en producción: solo Cobros protegida.
            await EjecutarAsync("""
                CREATE FUNCTION dbo.fnTenantAccess (@TenantId UNIQUEIDENTIFIER)
                RETURNS TABLE
                WITH SCHEMABINDING
                AS
                RETURN
                (
                    SELECT 1 AS fn_result
                    WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS UNIQUEIDENTIFIER)
                );
                """);

            await EjecutarAsync("""
                CREATE SECURITY POLICY dbo.TenantSecurityPolicy
                    ADD FILTER PREDICATE dbo.fnTenantAccess(TenantId) ON dbo.Cobros,
                    ADD BLOCK PREDICATE dbo.fnTenantAccess(TenantId) ON dbo.Cobros AFTER INSERT,
                    ADD BLOCK PREDICATE dbo.fnTenantAccess(TenantId) ON dbo.Cobros AFTER UPDATE
                    WITH (STATE = ON);
                """);

            // Datos sembrados ANTES de aplicar la migración: sin predicados todavía, así que se
            // insertan sin contexto de tenant. Una fila por tenant y por tabla.
            await EjecutarAsync($"""
                INSERT INTO dbo.LiquidacionesSemanales (Id, TenantId, MontoTotal)
                VALUES (1, '{TenantA}', 100), (2, '{TenantB}', 200);

                INSERT INTO dbo.LiquidacionesSemanalesDetalle (Id, TenantId, LiquidacionSemanalId)
                VALUES (1, '{TenantA}', 1), (2, '{TenantB}', 2);

                INSERT INTO dbo.LiquidacionesSemanalesDistribucionMensual (Id, TenantId, LiquidacionSemanalId)
                VALUES (1, '{TenantA}', 1), (2, '{TenantB}', 2);

                INSERT INTO dbo.ComprobantesCobro (Id, TenantId, TokenPublico, Total)
                VALUES (1, '{TenantA}', N'token-de-a-0000000000000000', 100),
                       (2, '{TenantB}', N'token-de-b-0000000000000000', 200);

                INSERT INTO dbo.ComprobanteCobroLineas (Id, TenantId, ComprobanteCobroId)
                VALUES (1, '{TenantA}', 1), (2, '{TenantB}', 2);
                """);

            // ── El SQL REAL de las migraciones, con la política encendida ──
            await EjecutarAsync(AddFinancialPhase4CobroSnapshotAndLiquidacionRls.ConstruirSql(agregar: true));
            await EjecutarAsync(AddFinancialPhase5CommissionSnapshotAndComprobanteRls.ConstruirSqlComprobantes(agregar: true));

            // Se aplican dos veces a propósito: las migraciones tienen que ser idempotentes.
            await EjecutarAsync(AddFinancialPhase4CobroSnapshotAndLiquidacionRls.ConstruirSql(agregar: true));
            await EjecutarAsync(AddFinancialPhase5CommissionSnapshotAndComprobanteRls.ConstruirSqlComprobantes(agregar: true));
        }

        public async Task DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await EjecutarEnMasterAsync(
                $"IF DB_ID(N'{_nombreBase}') IS NOT NULL BEGIN ALTER DATABASE [{_nombreBase}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_nombreBase}]; END");
        }


        public string InsertComprobanteSql(Guid tenantId, int id) =>
            $"INSERT INTO dbo.ComprobantesCobro (Id, TenantId, TokenPublico, Total) VALUES ({id}, '{tenantId}', N'token-{id}-0000000000000000', 1)";

        public string InsertLiquidacionSql(Guid tenantId, int id) =>
            $"INSERT INTO dbo.LiquidacionesSemanales (Id, TenantId, MontoTotal) VALUES ({id}, '{tenantId}', 1)";

        public Task EjecutarAsync(string sql) => EjecutarComoTenantAsync(null, sql);

        public async Task EjecutarComoTenantAsync(Guid? tenantId, string sql)
        {
            await using var connection = await AbrirAsync(tenantId);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public Task<T> ScalarAsync<T>(string sql) => ScalarComoTenantAsync<T>(null, sql);

        public async Task<T> ScalarComoTenantAsync<T>(Guid? tenantId, string sql)
        {
            await using var connection = await AbrirAsync(tenantId);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = await command.ExecuteScalarAsync();
            return (T)Convert.ChangeType(result!, typeof(T));
        }

        private async Task<SqlConnection> AbrirAsync(Guid? tenantId)
        {
            var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();

            if (tenantId.HasValue)
            {
                await using var setContext = connection.CreateCommand();
                setContext.CommandText = "EXEC sp_set_session_context @key = N'TenantId', @value = @tenant;";
                setContext.Parameters.AddWithValue("@tenant", tenantId.Value);
                await setContext.ExecuteNonQueryAsync();
            }

            return connection;
        }

        private static async Task EjecutarEnMasterAsync(string sql)
        {
            await using var connection = new SqlConnection(MasterConnection);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }
}
