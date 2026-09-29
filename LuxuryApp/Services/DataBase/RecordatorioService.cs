using System.Data;
using LuxuryApp.Models.DataBase;
using Microsoft.Data.SqlClient;

namespace LuxuryApp.Services.DataBase
{
    public class RecordatorioService
    {
        private readonly string _connectionString;

        public RecordatorioService(IConfiguration config)
        {
            _connectionString = config.GetConnectionString("ConexionSql")
                ?? throw new InvalidOperationException("La cadena de conexion 'ConexionSql' es obligatoria.");
        }

        public async Task<List<ClientesModel>> ObtenerUsuariosProximos()
        {
            var usuarios = new List<ClientesModel>();

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand("ObtenerCitasProximas", connection)
            {
                CommandType = CommandType.StoredProcedure
            };

            await connection.OpenAsync();

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                usuarios.Add(new ClientesModel
                {
                    Nombre = reader["Nombre"]?.ToString() ?? string.Empty,
                    CorreoElectronico = reader["CorreoElectronico"]?.ToString() ?? string.Empty
                });
            }

            return usuarios;
        }

        public async Task<List<ClientesModel>> ObtenerCumpleañerosHoy()
        {
            var usuarios = new List<ClientesModel>();

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(@"
        SELECT Nombre, CorreoElectronico, FechaCumpleaños
        FROM Clientes
        WHERE FechaCumpleaños IS NOT NULL
        AND DAY(FechaCumpleaños) = DAY(GETDATE())
        AND MONTH(FechaCumpleaños) = MONTH(GETDATE())", connection);

            await connection.OpenAsync();

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                usuarios.Add(new ClientesModel
                {
                    Nombre = reader["Nombre"]?.ToString() ?? string.Empty,
                    CorreoElectronico = reader["CorreoElectronico"]?.ToString() ?? string.Empty,
                    FechaCumpleaños = reader.GetDateTime(reader.GetOrdinal("FechaCumpleaños"))
                });
            }

            return usuarios;
        }
    }
}
