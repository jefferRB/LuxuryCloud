using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.Tenant;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Inversionistas
{
    /// <summary>
    /// Arma el documento del estado de cuenta desde el snapshot. Los datos del negocio (logo,
    /// teléfono, correo, dirección) se leen de la página pública del tenant, que es la fuente que
    /// el dueño ya mantiene; si no existe, el documento simplemente omite esos campos.
    /// </summary>
    public sealed class InvestorStatementDocumentService : IInvestorStatementDocumentService
    {
        private readonly ApplicationDbContext _context;
        private readonly ITenantDisplayNameService _tenantDisplayNameService;

        public InvestorStatementDocumentService(
            ApplicationDbContext context,
            ITenantDisplayNameService tenantDisplayNameService)
        {
            _context = context;
            _tenantDisplayNameService = tenantDisplayNameService;
        }

        public async Task<InvestorStatementDocument?> BuildAsync(
            int statementId,
            CancellationToken cancellationToken = default)
        {
            var statement = await _context.InvestorStatements
                .AsNoTracking()
                .Include(current => current.Investor)
                .FirstOrDefaultAsync(current => current.Id == statementId, cancellationToken);

            if (statement is null || statement.Investor is null)
            {
                return null;
            }

            var nombreNegocio = await _tenantDisplayNameService.GetCurrentTenantDisplayNameAsync(cancellationToken);

            var pagina = await _context.TenantPublicPages
                .AsNoTracking()
                .Select(page => new
                {
                    page.LogoUrl,
                    page.Phone,
                    page.Email,
                    page.Address
                })
                .FirstOrDefaultAsync(cancellationToken);

            return new InvestorStatementDocument
            {
                StatementId = statement.Id,
                TenantId = statement.TenantId,
                NombreNegocio = nombreNegocio,
                LogoUrl = NormalizeAbsoluteUrl(pagina?.LogoUrl),
                TelefonoNegocio = pagina?.Phone,
                EmailNegocio = pagina?.Email,
                DireccionNegocio = pagina?.Address,
                InversionistaNombre = statement.Investor.Nombre,
                InversionistaEmail = statement.Investor.Email,
                PeriodoInicio = statement.PeriodoInicio,
                PeriodoFin = statement.PeriodoFin,
                PeriodoEtiqueta = InvestorPeriodCalculator.BuildEtiqueta(
                    statement.Frecuencia,
                    statement.PeriodoInicio,
                    statement.PeriodoFin),
                IngresosNetos = statement.IngresosNetos,
                IvaExcluido = statement.IvaExcluido,
                GastosElegibles = statement.GastosElegibles,
                Liquidaciones = statement.Liquidaciones,
                AjustesPositivos = statement.AjustesPositivos,
                AjustesNegativos = statement.AjustesNegativos,
                PerdidaArrastrada = statement.PerdidaArrastrada,
                PerdidaPendiente = statement.PerdidaPendiente,
                GananciaDistribuible = statement.GananciaDistribuible,
                ParticipacionPorcentaje = statement.ParticipacionPorcentaje,
                ParticipacionCalculada = statement.ParticipacionCalculada,
                TotalPagado = statement.TotalPagado,
                SaldoPendiente = statement.SaldoPendiente,
                Estado = statement.Estado,
                EstadoTexto = statement.EstadoTexto,
                EstadoPagoTexto = ResolveEstadoPago(statement),
                FechaEmision = statement.FinalizadoAtUtc ?? statement.FechaCalculoUtc
            };
        }

        private static string ResolveEstadoPago(InvestorStatement statement)
        {
            if (statement.Estado == InvestorStatementStatus.Voided)
            {
                return "Anulado";
            }

            if (statement.ParticipacionCalculada <= 0m)
            {
                return "Sin monto a distribuir en este periodo";
            }

            if (statement.SaldoPendiente <= 0m && statement.TotalPagado > 0m)
            {
                return "Pagado en su totalidad";
            }

            return statement.TotalPagado > 0m
                ? "Pago parcial registrado"
                : "Pendiente de pago";
        }

        /// <summary>Solo URLs absolutas http(s) llegan al correo; una ruta relativa no se vería.</summary>
        private static string? NormalizeAbsoluteUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri.ToString()
                : null;
        }
    }
}
