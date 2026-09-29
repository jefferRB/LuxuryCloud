using LuxuryApp.Models.Finanzas;
using LuxuryApp.Services.BusinessTime;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Finanzas
{
    public sealed class EgresoQueryService : IEgresoQueryService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;

        public EgresoQueryService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
        }

        public async Task<EgresoIndexViewModel> BuildIndexViewModelAsync(
            EgresoFiltroViewModel filtros,
            bool includeFilterOptions = true,
            CancellationToken cancellationToken = default)
        {
            filtros ??= new EgresoFiltroViewModel();

            var filteredQuery = BuildFilteredEgresosQuery(filtros);

            var aggregate = await BuildAggregateAsync(filteredQuery, cancellationToken)
                ?? new EgresoAggregateProjection();

            var rows = await filteredQuery
                .OrderByDescending(e => e.FechaEgreso)
                .ThenByDescending(e => e.IdEgreso)
                .Select(e => new EgresoIndexItemViewModel
                {
                    IdEgreso = e.IdEgreso,
                    FechaEgreso = e.FechaEgreso,
                    CategoriaNombre = e.Categoria != null ? e.Categoria.Nombre ?? string.Empty : string.Empty,
                    Detalle = e.Detalle,
                    Monto = e.Monto,
                    MetodoPago = e.MetodoPago
                })
                .ToListAsync(cancellationToken);

            var viewModel = new EgresoIndexViewModel
            {
                Egresos = rows,
                Filtros = filtros,
                TotalEgresos = aggregate.TotalEgresos,
                CantidadRegistros = aggregate.CantidadRegistros
            };

            if (includeFilterOptions)
            {
                viewModel.Categorias = await GetCategoriasFiltroAsync(cancellationToken);
                viewModel.MetodosPago = EgresoPaymentCatalog.BuildSelectList();
            }

            return viewModel;
        }

        public async Task<EgresoViewModel> BuildCreateViewModelAsync(
            Egreso? egreso = null,
            CancellationToken cancellationToken = default)
        {
            var currentEgreso = egreso ?? new Egreso();

            if (currentEgreso.FechaEgreso == default)
            {
                currentEgreso.FechaEgreso = NormalizeEgresoDateTime(_businessDateTimeProvider.Now());
            }

            return await BuildFormViewModelAsync(currentEgreso, cancellationToken: cancellationToken);
        }

        public async Task<EgresoViewModel?> BuildEditViewModelAsync(
            int id,
            Egreso? egreso = null,
            CancellationToken cancellationToken = default)
        {
            var persistedEgreso = await _context.Egresos
                .AsNoTracking()
                .Where(e => e.IdEgreso == id)
                .Select(e => new Egreso
                {
                    IdEgreso = e.IdEgreso,
                    FechaEgreso = e.FechaEgreso,
                    Detalle = e.Detalle,
                    Monto = e.Monto,
                    MetodoPago = e.MetodoPago,
                    CategoriaId = e.CategoriaId
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (persistedEgreso is null)
            {
                return null;
            }

            var currentEgreso = egreso ?? persistedEgreso;
            currentEgreso.IdEgreso = persistedEgreso.IdEgreso;

            return await BuildFormViewModelAsync(
                currentEgreso,
                selectedCategoriaId: persistedEgreso.CategoriaId,
                cancellationToken);
        }

        private async Task<EgresoViewModel> BuildFormViewModelAsync(
            Egreso currentEgreso,
            int? selectedCategoriaId = null,
            CancellationToken cancellationToken = default)
        {
            selectedCategoriaId ??= currentEgreso.CategoriaId > 0 ? currentEgreso.CategoriaId : null;

            return new EgresoViewModel
            {
                Egreso = currentEgreso,
                // Las categorías técnicas del sistema (pago de liquidaciones, distribución a
                // inversionistas) NO se ofrecen: las asigna el flujo que genera esos egresos.
                // Se dejan visibles solo si YA son la categoría del egreso que se está editando,
                // para poder abrir la pantalla y moverlo a la categoría correcta.
                Categorias = await _context.Categorias
                    .AsNoTracking()
                    .Where(c => c.Activo || (selectedCategoriaId.HasValue && c.Id == selectedCategoriaId.Value))
                    .Where(c => (selectedCategoriaId.HasValue && c.Id == selectedCategoriaId.Value) ||
                                !(c.SystemCode != null
                                    ? c.SystemCode == SystemCategoryCodes.EmployeeSettlement ||
                                      c.SystemCode == SystemCategoryCodes.InvestorDistribution
                                    : c.Nombre == SystemCategoryCodes.NombreLegacyEmployeeSettlement ||
                                      c.Nombre == SystemCategoryCodes.NombreLegacyInvestorDistribution))
                    .OrderBy(c => c.Nombre)
                    .Select(c => new SelectListItem
                    {
                        Value = c.Id.ToString(),
                        Text = c.Nombre
                    })
                    .ToListAsync(cancellationToken),
                MetodosPago = EgresoPaymentCatalog.BuildSelectList()
            };
        }

        private IQueryable<Egreso> BuildFilteredEgresosQuery(EgresoFiltroViewModel filtros)
        {
            var query = _context.Egresos
                .AsNoTracking()
                .AsQueryable();

            if (filtros.CategoriaId.HasValue)
            {
                query = query.Where(e => e.CategoriaId == filtros.CategoriaId.Value);
            }

            var metodoPago = NormalizeMetodoPagoFilter(filtros.MetodoPago);
            if (!string.IsNullOrEmpty(metodoPago))
            {
                query = query.Where(e => e.MetodoPago == metodoPago);
            }

            var (fechaInicio, fechaFinExclusiva) = ResolveDateRange(filtros);
            if (fechaInicio.HasValue)
            {
                query = query.Where(e => e.FechaEgreso >= fechaInicio.Value);
            }

            if (fechaFinExclusiva.HasValue)
            {
                query = query.Where(e => e.FechaEgreso < fechaFinExclusiva.Value);
            }

            return query;
        }

        private Task<EgresoAggregateProjection?> BuildAggregateAsync(
            IQueryable<Egreso> filteredQuery,
            CancellationToken cancellationToken) =>
            filteredQuery
                .GroupBy(_ => 1)
                .Select(group => new EgresoAggregateProjection
                {
                    TotalEgresos = group.Sum(x => x.Monto),
                    CantidadRegistros = group.Count()
                })
                .SingleOrDefaultAsync(cancellationToken);

        private Task<List<SelectListItem>> GetCategoriasFiltroAsync(CancellationToken cancellationToken) =>
            _context.Categorias
                .AsNoTracking()
                .Where(c => c.Activo)
                .OrderBy(c => c.Nombre)
                .Select(c => new SelectListItem
                {
                    Value = c.Id.ToString(),
                    Text = c.Nombre
                })
                .ToListAsync(cancellationToken);

        private (DateTime? FechaInicio, DateTime? FechaFinExclusiva) ResolveDateRange(EgresoFiltroViewModel filtros)
        {
            var vistaTiempo = string.IsNullOrWhiteSpace(filtros.VistaTiempo)
                ? "dia"
                : filtros.VistaTiempo.Trim().ToLowerInvariant();

            var today = _businessDateTimeProvider.Today();

            return vistaTiempo switch
            {
                "todo" => (null, null),
                "dia" => (today, today.AddDays(1)),
                "semana" => ResolveWeekRange(today),
                "mes" => (new DateTime(today.Year, today.Month, 1), new DateTime(today.Year, today.Month, 1).AddMonths(1)),
                "anio" => (new DateTime(today.Year, 1, 1), new DateTime(today.Year, 1, 1).AddYears(1)),
                "fechas" => (filtros.FechaInicio?.Date, filtros.FechaFin?.Date.AddDays(1)),
                _ => (today, today.AddDays(1))
            };
        }

        private static (DateTime FechaInicio, DateTime FechaFinExclusiva) ResolveWeekRange(DateTime today)
        {
            var diff = (7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7;
            var inicioSemana = today.AddDays(-diff).Date;
            return (inicioSemana, inicioSemana.AddDays(7));
        }

        private static string NormalizeMetodoPagoFilter(string? metodoPago) =>
            string.IsNullOrWhiteSpace(metodoPago)
                ? string.Empty
                : metodoPago.Trim().ToUpperInvariant();

        private static DateTime NormalizeEgresoDateTime(DateTime value) =>
            new(
                value.Year,
                value.Month,
                value.Day,
                value.Hour,
                value.Minute,
                0);

        private sealed class EgresoAggregateProjection
        {
            public decimal TotalEgresos { get; init; }
            public int CantidadRegistros { get; init; }
        }
    }
}
