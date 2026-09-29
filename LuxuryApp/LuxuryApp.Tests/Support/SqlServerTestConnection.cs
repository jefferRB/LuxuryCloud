using Microsoft.Data.SqlClient;

namespace LuxuryApp.Tests.Support
{
    /// <summary>
    /// Conexión para los tests <c>Category=SqlServer</c> (interceptor de SESSION_CONTEXT y migraciones
    /// RLS). Nunca usa la configuración de la aplicación.
    ///
    /// <para>
    /// Por defecto apunta a la instancia local con autenticación integrada de Windows. En CI (o en
    /// cualquier máquina sin esa instancia) se define <c>LUXURYCLOUD_TEST_SQLSERVER</c> con una cadena
    /// de un SQL Server EFÍMERO de pruebas; el catálogo que traiga se ignora: cada test elige su base.
    /// </para>
    /// </summary>
    internal static class SqlServerTestConnection
    {
        public const string EnvironmentVariable = "LUXURYCLOUD_TEST_SQLSERVER";

        private const string LocalDefault =
            "Server=localhost;Database=master;Trusted_Connection=True;Encrypt=False;TrustServerCertificate=True;";

        public static string Master => ForDatabase("master");

        public static string ForDatabase(string database)
        {
            var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
            var builder = new SqlConnectionStringBuilder(
                string.IsNullOrWhiteSpace(configured) ? LocalDefault : configured)
            {
                InitialCatalog = database
            };

            return builder.ConnectionString;
        }
    }
}
