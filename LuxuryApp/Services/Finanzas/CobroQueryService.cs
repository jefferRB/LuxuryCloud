using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Fiscal;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Fiscal;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Finanzas
{
    /// <summary>
    /// Pantalla Ingresos (Cobros) y su exportación a Excel.
    ///
    /// <para>
    /// El desglose Base/IVA y la base de comisión salen del MISMO motor fiscal que usan el
    /// Dashboard y las liquidaciones (<see cref="ITaxCalculationService"/> +
    /// <see cref="ITenantFiscalConfigService"/>), aplicado POR LÍNEA de cobro y luego sumado.
    /// Antes este servicio dividía el total entre 1,13 de forma plana e ignoraba
    /// <c>AplicaIva</c>/<c>TarifaIva</c>/<c>PrecioIncluyeIva</c>, así que una venta exenta daba
    /// un IVA distinto acá y en el Dashboard para el mismo mes.
    /// </para>
    /// </summary>
    public sealed class CobroQueryService : ICobroQueryService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ITenantFiscalConfigService _fiscalConfig;
        private readonly ITaxCalculationService _taxService;

        public CobroQueryService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ITenantFiscalConfigService fiscalConfig,
            ITaxCalculationService taxService)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
            _fiscalConfig = fiscalConfig;
            _taxService = taxService;
        }

        public async Task<CobroIndexViewModel> BuildIndexViewModelAsync(
            CobroFiltroViewModel filtros,
            bool includeFilterOptions = true,
            CancellationToken cancellationToken = default)
        {
            filtros ??= new CobroFiltroViewModel();

            var filteredQuery = BuildFilteredCobrosQuery(filtros);
            var tenantFiscal = await _fiscalConfig.ObtenerAsync(cancellationToken);

            // 1) Agregados sobre TODO el filtro (no dependen de la página). El IVA se calcula POR
            //    LÍNEA (una venta exenta no se "des-IVA-iza"), así que no puede agregarse en SQL:
            //    se materializa una proyección ligera del periodo filtrado y se suma en memoria.
            var fiscalRows = await ProyectarFilasFiscales(filteredQuery).ToListAsync(cancellationToken);
            var aggregate = Agregar(fiscalRows, tenantFiscal);

            // 2) Paginación: el total filtrado ya lo conocemos (evita un COUNT extra).
            var totalRegistros = aggregate.Cantidad;
            var pageSize = NormalizePageSize(filtros.PageSize);
            var totalPaginas = totalRegistros == 0
                ? 1
                : (int)Math.Ceiling(totalRegistros / (double)pageSize);
            var page = Math.Clamp(filtros.Page < 1 ? 1 : filtros.Page, 1, totalPaginas);
            filtros.Page = page;
            filtros.PageSize = pageSize;

            // 3) Filas SOLO de la página actual (Skip/Take en backend, tras filtros y orden).
            var rows = await filteredQuery
                .OrderByDescending(c => c.FechaCobro)
                .ThenByDescending(c => c.IdCobro)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(c => new CobroIndexItemViewModel
                {
                    IdCobro = c.IdCobro,
                    FechaCobro = c.FechaCobro,
                    NombreCliente = c.NombreCliente,
                    FuncionarioNombre = c.Funcionario != null ? c.Funcionario.Nombre : string.Empty,
                    // Nombre histórico del cobro; el catálogo actual solo entra si es legacy.
                    Detalle = c.DetalleSnapshot != null
                        ? c.DetalleSnapshot
                        : (c.ServicioId != null
                            ? (c.Servicio != null ? c.Servicio.Nombre : "Sin detalle")
                            : (c.ServicioNombrePersonalizado != null
                                ? c.ServicioNombrePersonalizado
                                : (c.Producto != null ? c.Producto.NombreProducto : "Sin detalle"))),
                    Monto = c.Monto,
                    MetodoPago = c.MetodoPago,
                    EsServicio = c.ServicioId != null || c.ServicioNombrePersonalizado != null,
                    // Comprobante "vivo" más reciente del cobro (OUTER APPLY: una sola consulta).
                    ComprobanteId = _context.ComprobantesCobro
                        .Where(cc => cc.CobroId == c.IdCobro && cc.EstadoEnvio != Models.Comprobantes.ComprobanteEstadoEnvio.Cancelled)
                        .OrderByDescending(cc => cc.Id)
                        .Select(cc => (int?)cc.Id)
                        .FirstOrDefault(),
                    ComprobanteEstado = _context.ComprobantesCobro
                        .Where(cc => cc.CobroId == c.IdCobro && cc.EstadoEnvio != Models.Comprobantes.ComprobanteEstadoEnvio.Cancelled)
                        .OrderByDescending(cc => cc.Id)
                        .Select(cc => (Models.Comprobantes.ComprobanteEstadoEnvio?)cc.EstadoEnvio)
                        .FirstOrDefault(),
                    ComprobanteToken = _context.ComprobantesCobro
                        .Where(cc => cc.CobroId == c.IdCobro && cc.EstadoEnvio != Models.Comprobantes.ComprobanteEstadoEnvio.Cancelled)
                        .OrderByDescending(cc => cc.Id)
                        .Select(cc => cc.TokenPublico)
                        .FirstOrDefault(),
                    ComprobanteNumero = _context.ComprobantesCobro
                        .Where(cc => cc.CobroId == c.IdCobro && cc.EstadoEnvio != Models.Comprobantes.ComprobanteEstadoEnvio.Cancelled)
                        .OrderByDescending(cc => cc.Id)
                        .Select(cc => cc.NumeroInterno)
                        .FirstOrDefault(),
                    ComprobanteEmail = _context.ComprobantesCobro
                        .Where(cc => cc.CobroId == c.IdCobro && cc.EstadoEnvio != Models.Comprobantes.ComprobanteEstadoEnvio.Cancelled)
                        .OrderByDescending(cc => cc.Id)
                        .Select(cc => cc.EmailDestino)
                        .FirstOrDefault(),
                    ComprobanteSentAt = _context.ComprobantesCobro
                        .Where(cc => cc.CobroId == c.IdCobro && cc.EstadoEnvio != Models.Comprobantes.ComprobanteEstadoEnvio.Cancelled)
                        .OrderByDescending(cc => cc.Id)
                        .Select(cc => cc.SentAt)
                        .FirstOrDefault()
                })
                .ToListAsync(cancellationToken);

            var viewModel = BuildViewModelFromAggregate(aggregate, filtros);
            viewModel.Cobros = rows;
            viewModel.Page = page;
            viewModel.PageSize = pageSize;
            viewModel.TotalRegistros = totalRegistros;
            viewModel.TotalPaginas = totalPaginas;

            if (includeFilterOptions)
            {
                viewModel.Funcionarios = await GetFuncionariosFiltroAsync(cancellationToken);
                viewModel.MetodosPago = GetMetodosPago();
            }

            return viewModel;
        }

        public async Task<CobroViewModel> BuildCreateViewModelAsync(
            Cobro? cobro = null,
            CancellationToken cancellationToken = default)
        {
            var currentCobro = cobro ?? new Cobro();

            if (currentCobro.FechaCobro == default)
            {
                currentCobro.FechaCobro = NormalizeCobroDateTime(_businessDateTimeProvider.Now());
            }

            return await BuildFormViewModelAsync(currentCobro, cancellationToken: cancellationToken);
        }

        public async Task<CobroViewModel?> BuildEditViewModelAsync(
            int id,
            Cobro? cobro = null,
            CancellationToken cancellationToken = default)
        {
            var persistedCobro = await _context.Cobros
                .AsNoTracking()
                .Where(c => c.IdCobro == id)
                .Select(c => new Cobro
                {
                    IdCobro = c.IdCobro,
                    FechaCobro = c.FechaCobro,
                    NombreCliente = c.NombreCliente,
                    ClienteId = c.ClienteId,
                    FuncionarioId = c.FuncionarioId,
                    ServicioId = c.ServicioId,
                    ProductoId = c.ProductoId,
                    Monto = c.Monto,
                    MetodoPago = c.MetodoPago,
                    Observaciones = c.Observaciones
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (persistedCobro is null)
            {
                return null;
            }

            var currentCobro = cobro ?? persistedCobro;
            currentCobro.IdCobro = persistedCobro.IdCobro;

            if (persistedCobro.ProductoId.HasValue)
            {
                currentCobro.ProductoId = persistedCobro.ProductoId;
                currentCobro.ServicioId = null;
            }

            return await BuildFormViewModelAsync(
                currentCobro,
                selectedFuncionarioId: persistedCobro.FuncionarioId,
                selectedServicioId: persistedCobro.ServicioId,
                selectedProductoId: persistedCobro.ProductoId,
                cancellationToken);
        }

        private async Task<CobroViewModel> BuildFormViewModelAsync(
            Cobro currentCobro,
            int? selectedFuncionarioId = null,
            int? selectedServicioId = null,
            int? selectedProductoId = null,
            CancellationToken cancellationToken = default)
        {
            selectedFuncionarioId ??= currentCobro.FuncionarioId > 0 ? currentCobro.FuncionarioId : null;
            selectedServicioId ??= currentCobro.ServicioId;
            selectedProductoId ??= currentCobro.ProductoId;

            return new CobroViewModel
            {
                Cobro = currentCobro,
                Funcionarios = await _context.Funcionarios
                    .AsNoTracking()
                    .Where(f => f.Activo || (selectedFuncionarioId.HasValue && f.IdFuncionario == selectedFuncionarioId.Value))
                    .OrderBy(f => f.Nombre)
                    .Select(f => new SelectListItem
                    {
                        Value = f.IdFuncionario.ToString(),
                        Text = f.Nombre
                    })
                    .ToListAsync(cancellationToken),
                Servicios = await _context.Servicios
                    .AsNoTracking()
                    .Where(s => s.Activo || (selectedServicioId.HasValue && s.Id == selectedServicioId.Value))
                    .OrderBy(s => s.Nombre)
                    .Select(s => new SelectListItem
                    {
                        Value = s.Id.ToString(),
                        Text = s.Nombre
                    })
                    .ToListAsync(cancellationToken),
                Productos = await _context.Productos
                    .AsNoTracking()
                    .Where(p =>
                        (p.Activo && p.CantidadProducto > 0) ||
                        (selectedProductoId.HasValue && p.IdProducto == selectedProductoId.Value))
                    .OrderBy(p => p.NombreProducto)
                    .Select(p => new SelectListItem
                    {
                        Value = p.IdProducto.ToString(),
                        Text = p.NombreProducto
                    })
                    .ToListAsync(cancellationToken),
                MetodosPago = GetMetodosPago()
            };
        }

        public Task<decimal?> ObtenerPrecioServicioAsync(int id, CancellationToken cancellationToken = default) =>
            _context.Servicios
                .AsNoTracking()
                .Where(s => s.Id == id && s.Activo)
                .Select(s => (decimal?)s.Precio)
                .SingleOrDefaultAsync(cancellationToken);

        public Task<decimal?> ObtenerPrecioProductoAsync(int id, CancellationToken cancellationToken = default) =>
            _context.Productos
                .AsNoTracking()
                .Where(p => p.IdProducto == id && p.Activo)
                .Select(p => (decimal?)p.PrecioProducto)
                .SingleOrDefaultAsync(cancellationToken);

        private IQueryable<Cobro> BuildFilteredCobrosQuery(CobroFiltroViewModel filtros)
        {
            var query = _context.Cobros
                .AsNoTracking()
                .AsQueryable();

            if (filtros.FuncionarioId.HasValue)
            {
                query = query.Where(c => c.FuncionarioId == filtros.FuncionarioId.Value);
            }

            var metodoPago = NormalizeMetodoPagoFilter(filtros.MetodoPago);
            if (!string.IsNullOrEmpty(metodoPago))
            {
                query = query.Where(c => c.MetodoPago == metodoPago);
            }

            var (fechaInicio, fechaFinExclusiva) = ResolveDateRange(filtros);
            if (fechaInicio.HasValue)
            {
                query = query.Where(c => c.FechaCobro >= fechaInicio.Value);
            }

            if (fechaFinExclusiva.HasValue)
            {
                query = query.Where(c => c.FechaCobro < fechaFinExclusiva.Value);
            }

            if (filtros.MostrarServicios && !filtros.MostrarProductos)
            {
                query = query.Where(c => c.ServicioId != null || c.ServicioNombrePersonalizado != null);
            }
            else if (!filtros.MostrarServicios && filtros.MostrarProductos)
            {
                query = query.Where(c => c.ProductoId != null);
            }
            else if (!filtros.MostrarServicios && !filtros.MostrarProductos)
            {
                query = query.Where(c => false);
            }

            return query;
        }

        /// <summary>
        /// Proyección ligera con lo mínimo para el motor fiscal. La resolución de la configuración
        /// de cada línea (aplica IVA / tarifa / precio incluye IVA) es LA MISMA que usa
        /// <c>LiquidacionSemanalService</c>: si no hay servicio/producto de catálogo (servicio
        /// personalizado), la línea es gravada y hereda la configuración del tenant.
        /// </summary>
        private static IQueryable<CobroFiscalRow> ProyectarFilasFiscales(IQueryable<Cobro> filteredQuery) =>
            filteredQuery.Select(c => new CobroFiscalRow
            {
                Monto = c.Monto,
                EsServicio = c.ServicioId != null || c.ServicioNombrePersonalizado != null,
                EsProducto = c.ProductoId != null,
                MetodoPago = c.MetodoPago,
                // Las dos fuentes viajan juntas y CobroFiscalidadEfectiva decide: manda el
                // snapshot del cobro; el catálogo solo entra cuando el cobro es legacy.
                AplicaIvaSnapshot = c.AplicaIvaSnapshot,
                TarifaIvaSnapshot = c.TarifaIvaSnapshot,
                PrecioIncluyeIvaSnapshot = c.PrecioIncluyeIvaSnapshot,
                AplicaIvaCatalogo = c.ProductoId != null
                    ? (c.Producto == null || c.Producto.AplicaIva)
                    : (c.Servicio == null || c.Servicio.AplicaIva),
                TarifaIvaCatalogo = c.ProductoId != null
                    ? (c.Producto != null ? c.Producto.TarifaIva : null)
                    : (c.Servicio != null ? c.Servicio.TarifaIva : null),
                PrecioIncluyeIvaCatalogo = c.ProductoId != null
                    ? (c.Producto != null ? c.Producto.PrecioIncluyeIva : null)
                    : (c.Servicio != null ? c.Servicio.PrecioIncluyeIva : null),
                EsProductoParaComision = c.ProductoId != null,
                // Las dos fuentes de remuneración, igual que con la fiscalidad: manda el snapshot
                // del cobro y la configuración actual del colaborador solo cubre a los legacy.
                PorcentajeServicioSnapshot = c.PorcentajeServicioSnapshot,
                PorcentajeProductoSnapshot = c.PorcentajeProductoSnapshot,
                ComisionCalculadaSobreSnapshot = c.ComisionCalculadaSobreSnapshot,
                TipoRelacionColaboradorSnapshot = c.TipoRelacionColaboradorSnapshot,
                ModalidadIvaColaboradorSnapshot = c.ModalidadIvaColaboradorSnapshot,
                TarifaIvaColaboradorSnapshot = c.TarifaIvaColaboradorSnapshot,
                RemuneracionActual = new CobroRemuneracionEfectiva(
                    c.Funcionario != null ? c.Funcionario.PorcentajeGanancia : 0m,
                    c.Funcionario != null ? c.Funcionario.PorcentajeProducto : 0m,
                    c.Funcionario != null ? c.Funcionario.ComisionCalculadaSobre : ComisionCalculadaSobre.TotalCobrado,
                    c.Funcionario != null ? c.Funcionario.TipoRelacionColaborador : TipoRelacionColaborador.Empleado,
                    c.Funcionario != null ? c.Funcionario.ModalidadIvaColaborador : ModalidadIvaColaborador.NoFactura,
                    c.Funcionario != null ? c.Funcionario.TarifaIvaFacturaColaborador : 0m,
                    false)
            });

        /// <summary>
        /// Desglose fiscal de UNA línea con el motor canónico, más la comisión informativa del
        /// colaborador sobre la base que indique su configuración.
        ///
        /// <para>
        /// Ojo: <see cref="CobroLineaResultado.MontoColaborador"/> es un dato INFORMATIVO por línea.
        /// La planilla real (con IVA del colaborador y modalidades A/B/C) la calcula
        /// <c>ILiquidacionFuncionarioService</c> sobre el periodo completo; acá no se replica.
        /// </para>
        /// </summary>
        private CobroLineaResultado CalcularLinea(CobroFiscalRow fila, TenantFiscalConfig tenantFiscal)
        {
            var fiscal = fila.Fiscalidad();
            var linea = _fiscalConfig.ResolverLinea(
                fila.Monto, fiscal.AplicaIva, fiscal.TarifaIva, fiscal.PrecioIncluyeIva, tenantFiscal);

            var desglose = _taxService.Calcular(
                linea.TotalOrBase, linea.TaxRatePercent, linea.PriceIncludesTax, linea.Taxable);

            var baseComision = fila.ComisionSobre == ComisionCalculadaSobre.BaseSinIva
                ? desglose.NetBase
                : desglose.GrossTotal;

            return new CobroLineaResultado(
                desglose,
                FiscalMath.Redondear(baseComision * (fila.Porcentaje / 100m)));
        }

        /// <summary>
        /// Suma las líneas ya desglosadas. Política de agregación canónica: redondear POR LÍNEA y
        /// luego sumar, igual que <c>ITaxCalculationService.Sumar</c>. Así
        /// <c>TotalSinImpuestos + TotalImpuestos == TotalGenerado</c> se cumple por construcción y
        /// el resumen del Excel coincide exactamente con la suma de sus propias filas.
        /// </summary>
        private CobroAggregateProjection Agregar(
            IReadOnlyCollection<CobroFiscalRow> filas,
            TenantFiscalConfig tenantFiscal)
        {
            var aggregate = new CobroAggregateProjection { Cantidad = filas.Count };

            foreach (var fila in filas)
            {
                var (desglose, montoColaborador) = CalcularLinea(fila, tenantFiscal);

                aggregate.TotalGenerado += desglose.GrossTotal;
                aggregate.TotalSinImpuestos += desglose.NetBase;
                aggregate.TotalImpuestos += desglose.TaxAmount;
                aggregate.PagoColaboradores += montoColaborador;

                if (fila.EsServicio)
                {
                    aggregate.CantidadServicios++;
                    aggregate.TotalServicios += desglose.GrossTotal;
                }

                if (fila.EsProducto)
                {
                    aggregate.TotalProductos += desglose.GrossTotal;
                }

                switch (fila.MetodoPago)
                {
                    case "EFECTIVO":
                        aggregate.GananciaEfectivo += desglose.GrossTotal;
                        break;
                    case "TARJETA":
                        aggregate.GananciaTarjeta += desglose.GrossTotal;
                        break;
                    case "SINPE":
                        aggregate.GananciaSinpe += desglose.GrossTotal;
                        break;
                }
            }

            return aggregate;
        }

        public async Task<CobroExportViewModel> BuildExportAsync(
            CobroFiltroViewModel filtros,
            CancellationToken cancellationToken = default)
        {
            filtros ??= new CobroFiltroViewModel();

            var filteredQuery = BuildFilteredCobrosQuery(filtros);
            var tenantFiscal = await _fiscalConfig.ObtenerAsync(cancellationToken);

            // TODAS las filas filtradas (sin paginar); proyección ligera (sin subconsultas de
            // comprobante). Una sola consulta: el resumen se arma con estas mismas filas, así el
            // total del Excel es exactamente la suma de las filas que el Excel imprime.
            var raw = await ProyectarFilasExportacion(filteredQuery)
                .OrderByDescending(c => c.FechaCobro)
                .ThenByDescending(c => c.IdCobro)
                .ToListAsync(cancellationToken);

            var aggregate = Agregar(raw, tenantFiscal);
            var resumen = BuildViewModelFromAggregate(aggregate, filtros);
            resumen.TotalRegistros = aggregate.Cantidad;

            var filas = raw
                .Select(r =>
                {
                    var (desglose, montoColaborador) = CalcularLinea(r, tenantFiscal);

                    return new CobroExportRow
                    {
                        FechaCobro = r.FechaCobro,
                        NombreCliente = r.NombreCliente,
                        FuncionarioNombre = r.FuncionarioNombre,
                        EsServicio = r.EsServicio,
                        Detalle = r.Detalle,
                        MetodoPago = r.MetodoPago,
                        Monto = desglose.GrossTotal,
                        BaseSinIva = desglose.NetBase,
                        IvaIncluido = desglose.TaxAmount,
                        MontoColaborador = montoColaborador,
                        MontoNegocio = desglose.NetBase - montoColaborador
                    };
                })
                .ToList();

            return new CobroExportViewModel { Resumen = resumen, Filas = filas };
        }

        private static IQueryable<ExportProjection> ProyectarFilasExportacion(IQueryable<Cobro> filteredQuery) =>
            filteredQuery.Select(c => new ExportProjection
            {
                IdCobro = c.IdCobro,
                FechaCobro = c.FechaCobro,
                NombreCliente = c.NombreCliente,
                FuncionarioNombre = c.Funcionario != null ? c.Funcionario.Nombre : string.Empty,
                Detalle = c.DetalleSnapshot != null
                    ? c.DetalleSnapshot
                    : (c.ServicioId != null
                        ? (c.Servicio != null ? c.Servicio.Nombre : "Sin detalle")
                        : (c.ServicioNombrePersonalizado != null
                            ? c.ServicioNombrePersonalizado
                            : (c.Producto != null ? c.Producto.NombreProducto : "Sin detalle"))),
                Monto = c.Monto,
                EsServicio = c.ServicioId != null || c.ServicioNombrePersonalizado != null,
                EsProducto = c.ProductoId != null,
                MetodoPago = c.MetodoPago,
                // Las dos fuentes viajan juntas y CobroFiscalidadEfectiva decide: manda el
                // snapshot del cobro; el catálogo solo entra cuando el cobro es legacy.
                AplicaIvaSnapshot = c.AplicaIvaSnapshot,
                TarifaIvaSnapshot = c.TarifaIvaSnapshot,
                PrecioIncluyeIvaSnapshot = c.PrecioIncluyeIvaSnapshot,
                AplicaIvaCatalogo = c.ProductoId != null
                    ? (c.Producto == null || c.Producto.AplicaIva)
                    : (c.Servicio == null || c.Servicio.AplicaIva),
                TarifaIvaCatalogo = c.ProductoId != null
                    ? (c.Producto != null ? c.Producto.TarifaIva : null)
                    : (c.Servicio != null ? c.Servicio.TarifaIva : null),
                PrecioIncluyeIvaCatalogo = c.ProductoId != null
                    ? (c.Producto != null ? c.Producto.PrecioIncluyeIva : null)
                    : (c.Servicio != null ? c.Servicio.PrecioIncluyeIva : null),
                EsProductoParaComision = c.ProductoId != null,
                // Las dos fuentes de remuneración, igual que con la fiscalidad: manda el snapshot
                // del cobro y la configuración actual del colaborador solo cubre a los legacy.
                PorcentajeServicioSnapshot = c.PorcentajeServicioSnapshot,
                PorcentajeProductoSnapshot = c.PorcentajeProductoSnapshot,
                ComisionCalculadaSobreSnapshot = c.ComisionCalculadaSobreSnapshot,
                TipoRelacionColaboradorSnapshot = c.TipoRelacionColaboradorSnapshot,
                ModalidadIvaColaboradorSnapshot = c.ModalidadIvaColaboradorSnapshot,
                TarifaIvaColaboradorSnapshot = c.TarifaIvaColaboradorSnapshot,
                RemuneracionActual = new CobroRemuneracionEfectiva(
                    c.Funcionario != null ? c.Funcionario.PorcentajeGanancia : 0m,
                    c.Funcionario != null ? c.Funcionario.PorcentajeProducto : 0m,
                    c.Funcionario != null ? c.Funcionario.ComisionCalculadaSobre : ComisionCalculadaSobre.TotalCobrado,
                    c.Funcionario != null ? c.Funcionario.TipoRelacionColaborador : TipoRelacionColaborador.Empleado,
                    c.Funcionario != null ? c.Funcionario.ModalidadIvaColaborador : ModalidadIvaColaborador.NoFactura,
                    c.Funcionario != null ? c.Funcionario.TarifaIvaFacturaColaborador : 0m,
                    false)
            });

        private static CobroIndexViewModel BuildViewModelFromAggregate(
            CobroAggregateProjection aggregate,
            CobroFiltroViewModel filtros)
        {
            // Base e IVA ya vienen del motor fiscal canónico, línea por línea.
            var totalSinImpuestos = aggregate.TotalSinImpuestos;
            var totalImpuestos = aggregate.TotalImpuestos;

            return new CobroIndexViewModel
            {
                Filtros = filtros,
                TotalCobrado = aggregate.TotalGenerado,
                CantidadServicios = aggregate.CantidadServicios,
                TotalServicios = aggregate.TotalServicios,
                TotalProductos = aggregate.TotalProductos,
                TotalGenerado = aggregate.TotalGenerado,
                TotalImpuestos = totalImpuestos,
                TotalSinImpuestos = totalSinImpuestos,
                PagoColaboradores = aggregate.PagoColaboradores,
                GananciaNegocio = totalSinImpuestos - aggregate.PagoColaboradores,
                GananciaEfectivo = aggregate.GananciaEfectivo,
                GananciaTarjeta = aggregate.GananciaTarjeta,
                GananciaSinpe = aggregate.GananciaSinpe
            };
        }

        private static int NormalizePageSize(int pageSize) =>
            CobroIndexViewModel.PageSizeOptions.Contains(pageSize) ? pageSize : 20;

        private Task<List<SelectListItem>> GetFuncionariosFiltroAsync(CancellationToken cancellationToken) =>
            _context.Funcionarios
                .AsNoTracking()
                .OrderBy(f => f.Nombre)
                .Select(f => new SelectListItem
                {
                    Value = f.IdFuncionario.ToString(),
                    Text = f.Nombre
                })
                .ToListAsync(cancellationToken);

        private static List<SelectListItem> GetMetodosPago() =>
            new()
            {
                new SelectListItem { Value = "EFECTIVO", Text = "Efectivo" },
                new SelectListItem { Value = "TARJETA", Text = "Tarjeta" },
                new SelectListItem { Value = "SINPE", Text = "Sinpe" }
            };

        private (DateTime? FechaInicio, DateTime? FechaFinExclusiva) ResolveDateRange(CobroFiltroViewModel filtros)
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
                "fechas" => (
                    filtros.FechaInicio?.Date,
                    filtros.FechaFin?.Date.AddDays(1)),
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

        private static DateTime NormalizeCobroDateTime(DateTime value) =>
            new(
                value.Year,
                value.Month,
                value.Day,
                value.Hour,
                value.Minute,
                0);

        /// <summary>Datos mínimos de un cobro para resolver su fiscalidad y su comisión informativa.</summary>
        private class CobroFiscalRow : ICobroFiscalSnapshotOrigen, ICobroRemuneracionSnapshot
        {
            public decimal Monto { get; init; }
            public bool EsServicio { get; init; }
            public bool EsProducto { get; init; }
            public string MetodoPago { get; init; } = string.Empty;

            public bool? AplicaIvaSnapshot { get; init; }
            public decimal? TarifaIvaSnapshot { get; init; }
            public bool? PrecioIncluyeIvaSnapshot { get; init; }
            public bool AplicaIvaCatalogo { get; init; }
            public decimal? TarifaIvaCatalogo { get; init; }
            public bool? PrecioIncluyeIvaCatalogo { get; init; }

            public bool EsProductoParaComision { get; init; }

            public decimal? PorcentajeServicioSnapshot { get; init; }
            public decimal? PorcentajeProductoSnapshot { get; init; }
            public ComisionCalculadaSobre? ComisionCalculadaSobreSnapshot { get; init; }
            public TipoRelacionColaborador? TipoRelacionColaboradorSnapshot { get; init; }
            public ModalidadIvaColaborador? ModalidadIvaColaboradorSnapshot { get; init; }
            public decimal? TarifaIvaColaboradorSnapshot { get; init; }

            /// <summary>Configuración vigente del colaborador: el fallback de los cobros legacy.</summary>
            public CobroRemuneracionEfectiva RemuneracionActual { get; init; }

            public CobroRemuneracionEfectiva Remuneracion() =>
                CobroRemuneracionEfectiva.Resolver(this, RemuneracionActual);

            public ComisionCalculadaSobre ComisionSobre => Remuneracion().ComisionCalculadaSobre;

            public decimal Porcentaje
            {
                get
                {
                    var remuneracion = Remuneracion();
                    return EsProductoParaComision
                        ? remuneracion.PorcentajeProductos
                        : remuneracion.PorcentajeServicios;
                }
            }
        }

        /// <summary>Fila fiscal + las columnas que el Excel imprime.</summary>
        private sealed class ExportProjection : CobroFiscalRow
        {
            public int IdCobro { get; init; }
            public DateTime FechaCobro { get; init; }
            public string NombreCliente { get; init; } = string.Empty;
            public string FuncionarioNombre { get; init; } = string.Empty;
            public string Detalle { get; init; } = string.Empty;
        }

        private readonly record struct CobroLineaResultado(TaxBreakdown Desglose, decimal MontoColaborador);

        private sealed class CobroAggregateProjection
        {
            public int Cantidad { get; init; }
            public int CantidadServicios { get; set; }
            public decimal TotalServicios { get; set; }
            public decimal TotalProductos { get; set; }
            public decimal TotalGenerado { get; set; }
            public decimal TotalSinImpuestos { get; set; }
            public decimal TotalImpuestos { get; set; }
            public decimal PagoColaboradores { get; set; }
            public decimal GananciaEfectivo { get; set; }
            public decimal GananciaTarjeta { get; set; }
            public decimal GananciaSinpe { get; set; }
        }
    }
}
