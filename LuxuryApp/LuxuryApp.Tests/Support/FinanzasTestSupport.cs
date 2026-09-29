using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Fiscal;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Productos;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Support
{
    /// <summary>
    /// Semillas mínimas para los tests que comparan Dashboard, Ingresos y Excel sobre el MISMO
    /// conjunto de cobros. Viven acá (y no duplicadas en cada archivo) porque el punto de esos
    /// tests es justamente que los tres módulos vean exactamente las mismas filas.
    /// </summary>
    internal static class FinanzasTestSupport
    {
        public static async Task<Funcionario> SeedFuncionarioAsync(
            ApplicationDbContext context,
            string nombre,
            decimal porcentajeServicio = 50m,
            decimal porcentajeProducto = 10m,
            ComisionCalculadaSobre comisionSobre = ComisionCalculadaSobre.BaseSinIva,
            TipoRelacionColaborador tipoRelacion = TipoRelacionColaborador.Empleado,
            ModalidadIvaColaborador modalidadIva = ModalidadIvaColaborador.NoFactura)
        {
            var puesto = new Puesto
            {
                NombrePuesto = $"Puesto {Guid.NewGuid():N}",
                Detalle = "Operativo",
                Activo = true
            };

            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            var funcionario = new Funcionario
            {
                Nombre = nombre,
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#222222",
                PorcentajeGanancia = porcentajeServicio,
                PorcentajeProducto = porcentajeProducto,
                ComisionCalculadaSobre = comisionSobre,
                RebajarImpuestosAntesDeComision = comisionSobre == ComisionCalculadaSobre.BaseSinIva,
                TipoRelacionColaborador = tipoRelacion,
                ModalidadIvaColaborador = modalidadIva,
                FechaIngreso = new DateTime(2026, 1, 1),
                Activo = true
            };

            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }

        /// <summary>
        /// Servicio de catálogo. <paramref name="aplicaIva"/> false reproduce una venta EXENTA
        /// explícita (el caso que hoy Dashboard e Ingresos interpretan distinto).
        /// </summary>
        public static async Task<Servicio> SeedServicioAsync(
            ApplicationDbContext context,
            string nombre,
            decimal precio,
            bool aplicaIva = true,
            decimal? tarifaIva = null,
            bool? precioIncluyeIva = null)
        {
            var servicio = new Servicio
            {
                Nombre = nombre,
                Precio = precio,
                DuracionMinutos = 30,
                Activo = true,
                AplicaIva = aplicaIva,
                TarifaIva = tarifaIva,
                PrecioIncluyeIva = precioIncluyeIva
            };

            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();
            return servicio;
        }

        public static async Task<Producto> SeedProductoAsync(
            ApplicationDbContext context,
            string nombre,
            decimal precio,
            bool aplicaIva = true,
            decimal? tarifaIva = null,
            bool? precioIncluyeIva = null,
            int stock = 10)
        {
            var producto = new Producto
            {
                NombreProducto = nombre,
                PrecioProducto = precio,
                CantidadProducto = stock,
                Activo = true,
                FechaRegistro = new DateTime(2026, 1, 1),
                AplicaIva = aplicaIva,
                TarifaIva = tarifaIva,
                PrecioIncluyeIva = precioIncluyeIva
            };

            context.Productos.Add(producto);
            await context.SaveChangesAsync();
            return producto;
        }

        public static Task<Cobro> SeedCobroServicioAsync(
            ApplicationDbContext context,
            Funcionario funcionario,
            Servicio servicio,
            DateTime fecha,
            decimal monto,
            string metodoPago = "EFECTIVO",
            string cliente = "Cliente") =>
            AddCobroAsync(context, new Cobro
            {
                FechaCobro = fecha,
                NombreCliente = cliente,
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = servicio.Id,
                Monto = monto,
                MetodoPago = metodoPago
            });

        /// <summary>
        /// Cobro de servicio PERSONALIZADO: nace de una cita fuera de catálogo, así que no tiene
        /// ServicioId ni ProductoId, solo el nombre. A efectos financieros es un SERVICIO.
        /// </summary>
        public static Task<Cobro> SeedCobroServicioPersonalizadoAsync(
            ApplicationDbContext context,
            Funcionario funcionario,
            string nombreServicio,
            DateTime fecha,
            decimal monto,
            string metodoPago = "EFECTIVO",
            string cliente = "Cliente") =>
            AddCobroAsync(context, new Cobro
            {
                FechaCobro = fecha,
                NombreCliente = cliente,
                FuncionarioId = funcionario.IdFuncionario,
                ServicioId = null,
                ProductoId = null,
                ServicioNombrePersonalizado = nombreServicio,
                Monto = monto,
                MetodoPago = metodoPago
            });

        public static Task<Cobro> SeedCobroProductoAsync(
            ApplicationDbContext context,
            Funcionario funcionario,
            Producto producto,
            DateTime fecha,
            decimal monto,
            string metodoPago = "EFECTIVO",
            string cliente = "Cliente") =>
            AddCobroAsync(context, new Cobro
            {
                FechaCobro = fecha,
                NombreCliente = cliente,
                FuncionarioId = funcionario.IdFuncionario,
                ProductoId = producto.IdProducto,
                Monto = monto,
                MetodoPago = metodoPago
            });

        public static async Task<Categoria> SeedCategoriaAsync(
            ApplicationDbContext context,
            string nombre,
            string detalle = "Categoría de prueba")
        {
            var categoria = new Categoria
            {
                Nombre = nombre,
                Detalle = detalle,
                Activo = true
            };

            context.Categorias.Add(categoria);
            await context.SaveChangesAsync();
            return categoria;
        }

        public static async Task<Egreso> SeedEgresoAsync(
            ApplicationDbContext context,
            Categoria categoria,
            DateTime fecha,
            decimal monto,
            string detalle,
            string metodoPago = "EFECTIVO")
        {
            var egreso = new Egreso
            {
                CategoriaId = categoria.Id,
                FechaEgreso = fecha,
                Monto = monto,
                MetodoPago = metodoPago,
                Detalle = detalle
            };

            context.Egresos.Add(egreso);
            await context.SaveChangesAsync();
            return egreso;
        }

        /// <summary>Registra un pago de liquidación por el camino real del dominio.</summary>
        public static Task<int> PagarAsync(
            ApplicationDbContext context,
            TestTenantProvider tenantProvider,
            Funcionario funcionario,
            DateTime semanaInicio,
            DateTime semanaFin,
            DateTime fechaPago,
            decimal monto,
            Guid? idempotencyKey = null,
            LuxuryApp.Services.Platform.IPlatformAuditService? auditService = null)
        {
            var service = ControllerTestSupport.CreateLiquidacionSemanalService(
                context, tenantProvider, auditService);

            return service.RegistrarPagoAsync(new LuxuryApp.Services.Funcionarios.RegistrarLiquidacionSemanalCommand
            {
                SemanaInicio = semanaInicio,
                SemanaFin = semanaFin,
                FechaPago = fechaPago,
                MetodoPago = "EFECTIVO",
                CreadoPor = "test",
                IdempotencyKey = idempotencyKey,
                Detalles =
                {
                    new LuxuryApp.Services.Funcionarios.RegistrarLiquidacionSemanalDetalleCommand
                    {
                        FuncionarioId = funcionario.IdFuncionario,
                        MontoPagado = monto
                    }
                }
            });
        }

        /// <summary>Filtro de Ingresos equivalente a "todo el mes", para comparar contra el Dashboard.</summary>
        public static CobroFiltroViewModel FiltroMes(int anio, int mes) =>
            new()
            {
                VistaTiempo = "fechas",
                FechaInicio = new DateTime(anio, mes, 1),
                FechaFin = new DateTime(anio, mes, DateTime.DaysInMonth(anio, mes)),
                PageSize = 100
            };

        private static async Task<Cobro> AddCobroAsync(ApplicationDbContext context, Cobro cobro)
        {
            context.Cobros.Add(cobro);
            await context.SaveChangesAsync();
            return cobro;
        }
    }
}
