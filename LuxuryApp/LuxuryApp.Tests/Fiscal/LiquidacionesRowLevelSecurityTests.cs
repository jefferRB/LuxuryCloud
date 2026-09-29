using LuxuryApp.Migrations;
using Microsoft.Data.SqlClient;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// RLS REAL contra SQL Server, no el filtro global de EF.
    ///
    /// <para>
    /// La auditoría de la Fase 3 encontró que <c>LiquidacionesSemanales</c>,
    /// <c>LiquidacionesSemanalesDetalle</c> y <c>LiquidacionesSemanalesDistribucionMensual</c>
    /// tenían TenantId pero ningún predicado de seguridad, mientras el resto de las tablas
    /// financieras sí. Su aislamiento dependía solo del filtro de EF, que es fail-open cuando no
    /// hay tenant resuelto: una consulta sin contexto veía las liquidaciones de TODOS los negocios.
    /// </para>
    ///
    /// <para>
    /// Estos tests ejecutan el SQL EXACTO de la migración
    /// <see cref="AddFinancialPhase4CobroSnapshotAndLiquidacionRls"/> sobre una base temporal y
    /// comprueban el comportamiento real del motor. Si alguien cambia ese SQL y rompe el
    /// aislamiento, acá se cae.
    /// </para>
    ///
    /// <para>Requieren SQL Server en localhost, igual que <c>TenantExecutionInfrastructureTests</c>.</para>
    /// </summary>
    [Collection(nameof(LiquidacionesRlsDatabase))]
    [Trait("Category", "SqlServer")]
    public class LiquidacionesRowLevelSecurityTests
    {
        private readonly LiquidacionesRlsDatabase _db;

        public LiquidacionesRowLevelSecurityTests(LiquidacionesRlsDatabase db) => _db = db;

        public static TheoryData<string> TablasProtegidas => new()
        {
            "LiquidacionesSemanales",
            "LiquidacionesSemanalesDetalle",
            "LiquidacionesSemanalesDistribucionMensual"
        };

        /// <summary>
        /// Las tres quedaron cubiertas con el patrón completo: FILTER + BLOCK INSERT + BLOCK UPDATE,
        /// el mismo que ya protegía al resto de las tablas financieras.
        /// </summary>
        [Fact]
        public async Task TenantSecurityCoverage_IncluyeLasTresTablas()
        {
            foreach (var tabla in new[]
                     {
                         "LiquidacionesSemanales",
                         "LiquidacionesSemanalesDetalle",
                         "LiquidacionesSemanalesDistribucionMensual"
                     })
            {
                var filtro = await _db.ScalarAsync<int>(
                    $"SELECT COUNT(*) FROM sys.security_predicates WHERE target_object_id = OBJECT_ID(N'dbo.{tabla}') AND predicate_type = 0");
                var blockInsert = await _db.ScalarAsync<int>(
                    $"SELECT COUNT(*) FROM sys.security_predicates WHERE target_object_id = OBJECT_ID(N'dbo.{tabla}') AND predicate_type = 1 AND operation = 1");
                var blockUpdate = await _db.ScalarAsync<int>(
                    $"SELECT COUNT(*) FROM sys.security_predicates WHERE target_object_id = OBJECT_ID(N'dbo.{tabla}') AND predicate_type = 1 AND operation = 2");

                Assert.Equal(1, filtro);
                Assert.Equal(1, blockInsert);
                Assert.Equal(1, blockUpdate);
            }

            // Y la política siguió ENCENDIDA todo el tiempo: agregar predicados no exige apagarla,
            // así que el despliegue no abre una ventana sin aislamiento.
            Assert.Equal(1, await _db.ScalarAsync<int>(
                "SELECT CAST(is_enabled AS int) FROM sys.security_policies WHERE name = N'TenantSecurityPolicy'"));
        }

        [Fact]
        public Task LiquidacionesSemanales_Rls_BloqueaLecturaOtroTenant() =>
            AssertNoVeLoDelOtroAsync("LiquidacionesSemanales");

        [Fact]
        public Task LiquidacionesSemanalesDetalle_Rls_BloqueaLecturaOtroTenant() =>
            AssertNoVeLoDelOtroAsync("LiquidacionesSemanalesDetalle");

        [Fact]
        public Task LiquidacionesSemanalesDistribucion_Rls_BloqueaLecturaOtroTenant() =>
            AssertNoVeLoDelOtroAsync("LiquidacionesSemanalesDistribucionMensual");

        /// <summary>
        /// Conocer el GUID de otro tenant no alcanza: el BLOCK PREDICATE rechaza el INSERT aunque
        /// el atacante escriba el TenantId ajeno a mano.
        /// </summary>
        [Fact]
        public async Task LiquidacionesSemanales_Rls_BloqueaInsertOtroTenant()
        {
            await Assert.ThrowsAsync<SqlException>(() =>
                _db.EjecutarComoTenantAsync(
                    LiquidacionesRlsDatabase.TenantA,
                    _db.InsertLiquidacionSql(LiquidacionesRlsDatabase.TenantB, id: 9001)));

            // Lo que importa es la CONDUCTA, no el texto del error (que además cambia de idioma):
            // la fila no existe para nadie.
            Assert.Equal(0, await _db.ScalarComoTenantAsync<int>(
                LiquidacionesRlsDatabase.TenantB,
                "SELECT COUNT(*) FROM dbo.LiquidacionesSemanales WHERE Id = 9001"));
        }

        /// <summary>
        /// Tampoco se puede EMPUJAR una fila propia hacia otro tenant para después leerla desde allá.
        /// </summary>
        [Fact]
        public async Task LiquidacionesSemanales_Rls_BloqueaUpdateOtroTenant()
        {
            await Assert.ThrowsAsync<SqlException>(() =>
                _db.EjecutarComoTenantAsync(
                    LiquidacionesRlsDatabase.TenantA,
                    $"UPDATE dbo.LiquidacionesSemanales SET TenantId = '{LiquidacionesRlsDatabase.TenantB}' WHERE Id = 1"));

            // La fila siguió siendo de A: el rechazo no dejó un estado a medias.
            Assert.Equal(1, await _db.ScalarComoTenantAsync<int>(
                LiquidacionesRlsDatabase.TenantA,
                "SELECT COUNT(*) FROM dbo.LiquidacionesSemanales WHERE Id = 1"));
        }

        /// <summary>El aislamiento no puede romper la operación normal del propio tenant.</summary>
        [Fact]
        public async Task MismoTenant_PuedeLeerEscribirLiquidaciones()
        {
            await _db.EjecutarComoTenantAsync(
                LiquidacionesRlsDatabase.TenantA,
                _db.InsertLiquidacionSql(LiquidacionesRlsDatabase.TenantA, id: 500));

            Assert.Equal(1, await _db.ScalarComoTenantAsync<int>(
                LiquidacionesRlsDatabase.TenantA,
                "SELECT COUNT(*) FROM dbo.LiquidacionesSemanales WHERE Id = 500"));

            await _db.EjecutarComoTenantAsync(
                LiquidacionesRlsDatabase.TenantA,
                "UPDATE dbo.LiquidacionesSemanales SET MontoTotal = 999 WHERE Id = 500");

            Assert.Equal(999m, await _db.ScalarComoTenantAsync<decimal>(
                LiquidacionesRlsDatabase.TenantA,
                "SELECT MontoTotal FROM dbo.LiquidacionesSemanales WHERE Id = 500"));
        }

        /// <summary>
        /// El caso que motivó todo: SIN TenantId en la sesión, el filtro global de EF es fail-open
        /// (Guid.Empty ⇒ true) y dejaba pasar las filas de todos los negocios. El RLS de SQL Server
        /// no tiene esa puerta: sin contexto no se ve NADA.
        /// </summary>
        [Fact]
        public async Task TenantSinContexto_NoDebeExponerLiquidaciones()
        {
            Assert.Equal(0, await _db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.LiquidacionesSemanales"));
            Assert.Equal(0, await _db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.LiquidacionesSemanalesDetalle"));
            Assert.Equal(0, await _db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.LiquidacionesSemanalesDistribucionMensual"));
        }

        // ─────────────── Comprobantes: BLOQUEO DE ESCRITURA ───────────────
        //
        // Los comprobantes reciben BLOCK pero NO FILTER, y es una decisión deliberada: la ruta
        // pública /comprobantes/{token} es anónima, no tiene TenantId en la sesión, y un FILTER
        // dejaría esa consulta en cero filas rompiendo todos los comprobantes ya enviados.

        [Fact]
        public async Task ComprobantesCobro_Rls_BloqueaInsertOtroTenant()
        {
            await Assert.ThrowsAsync<SqlException>(() =>
                _db.EjecutarComoTenantAsync(
                    LiquidacionesRlsDatabase.TenantA,
                    _db.InsertComprobanteSql(LiquidacionesRlsDatabase.TenantB, id: 9101)));

            Assert.Equal(0, await _db.ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.ComprobantesCobro WHERE Id = 9101"));
        }

        [Fact]
        public async Task ComprobantesCobro_Rls_BloqueaUpdateOtroTenant()
        {
            await Assert.ThrowsAsync<SqlException>(() =>
                _db.EjecutarComoTenantAsync(
                    LiquidacionesRlsDatabase.TenantA,
                    $"UPDATE dbo.ComprobantesCobro SET TenantId = '{LiquidacionesRlsDatabase.TenantB}' WHERE Id = 1"));

            Assert.Equal(1, await _db.ScalarAsync<int>(
                $"SELECT COUNT(*) FROM dbo.ComprobantesCobro WHERE Id = 1 AND TenantId = '{LiquidacionesRlsDatabase.TenantA}'"));
        }

        [Fact]
        public async Task ComprobanteCobroLineas_Rls_BloqueaInsertOtroTenant()
        {
            await Assert.ThrowsAsync<SqlException>(() =>
                _db.EjecutarComoTenantAsync(
                    LiquidacionesRlsDatabase.TenantA,
                    $"INSERT INTO dbo.ComprobanteCobroLineas (Id, TenantId, ComprobanteCobroId) VALUES (9102, '{LiquidacionesRlsDatabase.TenantB}', 2)"));

            Assert.Equal(0, await _db.ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.ComprobanteCobroLineas WHERE Id = 9102"));
        }

        [Fact]
        public async Task Comprobantes_MismoTenant_FuncionaNormal()
        {
            await _db.EjecutarComoTenantAsync(
                LiquidacionesRlsDatabase.TenantA,
                _db.InsertComprobanteSql(LiquidacionesRlsDatabase.TenantA, id: 9103));

            await _db.EjecutarComoTenantAsync(
                LiquidacionesRlsDatabase.TenantA,
                "UPDATE dbo.ComprobantesCobro SET Total = 777 WHERE Id = 9103");

            Assert.Equal(777m, await _db.ScalarAsync<decimal>(
                "SELECT Total FROM dbo.ComprobantesCobro WHERE Id = 9103"));
        }

        /// <summary>
        /// La contracara de la decisión: SIN FILTER, la ruta pública anónima sigue pudiendo leer el
        /// comprobante por su token. Si alguien agrega un FILTER más adelante, este test se cae y
        /// avisa que acaba de romper los comprobantes ya enviados a los clientes.
        /// </summary>
        [Fact]
        public async Task ComprobantesCobro_SinContexto_SiguenLegiblesPorToken()
        {
            Assert.Equal(1, await _db.ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.ComprobantesCobro WHERE TokenPublico = N'token-de-a-0000000000000000'"));
        }

        /// <summary>
        /// §34.29 — Cobertura: ninguna de las tablas financieras del alcance queda sin proteger.
        /// Comprobantes solo con BLOCK, por lo explicado arriba.
        /// </summary>
        [Fact]
        public async Task TenantSecurityCoverage_NoReportaTablasFinancierasFaltantes()
        {
            foreach (var tabla in new[] { "ComprobantesCobro", "ComprobanteCobroLineas" })
            {
                Assert.Equal(1, await _db.ScalarAsync<int>(
                    $"SELECT COUNT(*) FROM sys.security_predicates WHERE target_object_id = OBJECT_ID(N'dbo.{tabla}') AND predicate_type = 1 AND operation = 1"));
                Assert.Equal(1, await _db.ScalarAsync<int>(
                    $"SELECT COUNT(*) FROM sys.security_predicates WHERE target_object_id = OBJECT_ID(N'dbo.{tabla}') AND predicate_type = 1 AND operation = 2"));
            }

            Assert.Equal(1, await _db.ScalarAsync<int>(
                "SELECT CAST(is_enabled AS int) FROM sys.security_policies WHERE name = N'TenantSecurityPolicy'"));
        }

        private async Task AssertNoVeLoDelOtroAsync(string tabla)
        {
            // A ve lo suyo…
            Assert.Equal(1, await _db.ScalarComoTenantAsync<int>(
                LiquidacionesRlsDatabase.TenantA, $"SELECT COUNT(*) FROM dbo.{tabla}"));

            // …y B ve lo suyo, nunca lo de A.
            Assert.Equal(1, await _db.ScalarComoTenantAsync<int>(
                LiquidacionesRlsDatabase.TenantB, $"SELECT COUNT(*) FROM dbo.{tabla}"));

            Assert.Equal(0, await _db.ScalarComoTenantAsync<int>(
                LiquidacionesRlsDatabase.TenantA,
                $"SELECT COUNT(*) FROM dbo.{tabla} WHERE TenantId = '{LiquidacionesRlsDatabase.TenantB}'"));
        }
    }
}
