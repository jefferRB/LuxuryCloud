using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LuxuryApp.Services.Tenant
{
    public sealed class TenantSessionConnectionInterceptor : DbConnectionInterceptor
    {
        private readonly ITenantProvider _tenantProvider;
        private readonly ILogger<TenantSessionConnectionInterceptor> _logger;

        public TenantSessionConnectionInterceptor(
            ITenantProvider tenantProvider,
            ILogger<TenantSessionConnectionInterceptor> logger)
        {
            _tenantProvider = tenantProvider;
            _logger = logger;
        }

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            ApplyTenantContext(connection);
            base.ConnectionOpened(connection, eventData);
        }

        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await ApplyTenantContextAsync(connection, cancellationToken);
            await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
        }

        public override InterceptionResult ConnectionClosing(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result)
        {
            TryClearTenantContext(connection);
            return base.ConnectionClosing(connection, eventData, result);
        }

        public override async ValueTask<InterceptionResult> ConnectionClosingAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result)
        {
            await TryClearTenantContextAsync(connection);
            return await base.ConnectionClosingAsync(connection, eventData, result);
        }

        private void ApplyTenantContext(DbConnection connection)
        {
            using var command = CreateCommand(connection);
            command.Parameters.Add(CreateTenantParameter(command));
            command.ExecuteNonQuery();
        }

        private async Task ApplyTenantContextAsync(DbConnection connection, CancellationToken cancellationToken)
        {
            await using var command = CreateCommand(connection);
            command.Parameters.Add(CreateTenantParameter(command));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private void TryClearTenantContext(DbConnection connection)
        {
            try
            {
                using var command = CreateCommand(connection);
                command.Parameters.Add(CreateNullTenantParameter(command));
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No fue posible limpiar SESSION_CONTEXT TenantId antes de devolver la conexion al pool.");
            }
        }

        private async Task TryClearTenantContextAsync(DbConnection connection)
        {
            try
            {
                await using var command = CreateCommand(connection);
                command.Parameters.Add(CreateNullTenantParameter(command));
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No fue posible limpiar SESSION_CONTEXT TenantId antes de devolver la conexion al pool.");
            }
        }

        private DbCommand CreateCommand(DbConnection connection)
        {
            var command = connection.CreateCommand();
            command.CommandText = "EXEC sp_set_session_context @key=N'TenantId', @value=@tenantId";
            return command;
        }

        private DbParameter CreateTenantParameter(DbCommand command)
        {
            if (!_tenantProvider.HasTenant())
            {
                _logger.LogDebug("SESSION_CONTEXT TenantId limpiado para una conexion sin tenant resuelto.");
                return CreateNullTenantParameter(command);
            }

            var parameter = command.CreateParameter();
            parameter.ParameterName = "@tenantId";
            parameter.Value = _tenantProvider.GetTenantId();
            return parameter;
        }

        private static DbParameter CreateNullTenantParameter(DbCommand command)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@tenantId";
            parameter.Value = DBNull.Value;
            return parameter;
        }
    }
}
