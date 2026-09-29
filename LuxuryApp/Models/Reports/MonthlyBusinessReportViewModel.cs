namespace LuxuryApp.Models.Reports
{
    /// <summary>
    /// DTO central del Resumen Ejecutivo Mensual. Se construye reutilizando los servicios
    /// del Dashboard Financiero y de Información del negocio (no duplica consultas) y es la
    /// única fuente que consume la plantilla del correo.
    /// </summary>
    public sealed class MonthlyBusinessReportViewModel
    {
        // ─────────────── Datos generales ───────────────

        public Guid TenantId { get; set; }

        public string NombreNegocio { get; set; } = string.Empty;

        public int Mes { get; set; }

        public int Anio { get; set; }

        /// <summary>Nombre del mes en español, capitalizado (ej. "Junio").</summary>
        public string MesNombre { get; set; } = string.Empty;

        /// <summary>Fecha/hora local del negocio en que se generó el reporte.</summary>
        public DateTime FechaGeneracion { get; set; }

        /// <summary>false cuando el mes no registró ingresos, citas ni productos.</summary>
        public bool TieneActividad { get; set; }

        // ─────────────── Resumen financiero ───────────────

        public decimal Ingresos { get; set; }

        /// <summary>
        /// COSTOS Y GASTOS DEVENGADOS del mes: liquidaciones generadas por la producción del
        /// período + gastos operativos elegibles. Es la línea que resta la ganancia.
        /// </summary>
        public decimal Egresos { get; set; }

        /// <summary>
        /// GANANCIA DEL MES. Es exactamente la misma métrica que muestra el Dashboard
        /// (<c>DashboardViewModel.ResultadoAnalitico</c>), servida por el motor único de ganancia.
        ///
        /// <para>
        /// Antes acá viajaba el resultado de CAJA (ingresos netos − salidas de caja), que no es la
        /// ganancia: mezcla base devengada con pagos que pueden corresponder a otro período.
        /// </para>
        /// </summary>
        public decimal GananciaReal { get; set; }

        /// <summary>
        /// CAJA: egresos registrados con fecha dentro del mes. Se informa aparte, nunca como
        /// ganancia. Puede diferir de <see cref="Egresos"/> y eso es correcto.
        /// </summary>
        public decimal SalidasCaja { get; set; }

        /// <summary>Margen sobre ingresos, en porcentaje (0 cuando no hay ingresos).</summary>
        public decimal MargenGanancia { get; set; }

        public decimal Impuestos { get; set; }

        public decimal PagoFuncionarios { get; set; }

        public decimal TotalSinImpuestos { get; set; }

        public decimal ServiciosGeneradosMonto { get; set; }

        public decimal ProductosGeneradosMonto { get; set; }

        public decimal IngresosEfectivo { get; set; }

        public decimal IngresosSinpe { get; set; }

        public decimal IngresosTarjeta { get; set; }

        // ─────────────── Actividad del negocio (mes seleccionado) ───────────────

        public int ServiciosRealizados { get; set; }

        public int ProductosVendidos { get; set; }

        public int CitasOnlineReservadas { get; set; }

        /// <summary>Vacío cuando el mes no tuvo servicios (la plantilla muestra "Sin datos suficientes").</summary>
        public string ServicioMasSolicitadoNombre { get; set; } = string.Empty;

        public int ServicioMasSolicitadoCantidad { get; set; }

        public string ProductoMasVendidoNombre { get; set; } = string.Empty;

        public int ProductoMasVendidoCantidad { get; set; }

        public string FuncionarioEstrellaNombre { get; set; } = string.Empty;

        public int FuncionarioEstrellaCantidadCitas { get; set; }

        // ─────────────── Comportamiento operativo ───────────────
        // Igual que la vista Información: los extremos de día/hora se calculan sobre el
        // histórico de citas del negocio, no solo del mes seleccionado.

        public string DiaMasOcupado { get; set; } = string.Empty;

        public int DiaMasOcupadoCantidad { get; set; }

        public string DiaMenosOcupado { get; set; } = string.Empty;

        public int DiaMenosOcupadoCantidad { get; set; }

        public string HoraMasOcupada { get; set; } = string.Empty;

        public int HoraMasOcupadaCantidad { get; set; }

        public string HoraMenosOcupada { get; set; } = string.Empty;

        public int HoraMenosOcupadaCantidad { get; set; }

        // ─────────────── Comparación contra el mes anterior ───────────────

        /// <summary>false cuando el mes anterior no tuvo actividad (no hay base para comparar).</summary>
        public bool TieneComparativa { get; set; }

        public string MesAnteriorNombre { get; set; } = string.Empty;

        public decimal IngresosMesAnterior { get; set; }

        public decimal EgresosMesAnterior { get; set; }

        public decimal GananciaRealMesAnterior { get; set; }

        public int ServiciosRealizadosMesAnterior { get; set; }

        public int ProductosVendidosMesAnterior { get; set; }

        public int CitasOnlineMesAnterior { get; set; }

        // Variación en % (null = no comparable: el mes anterior estaba en cero).
        public decimal? VariacionIngresosPorcentaje { get; set; }

        public decimal? VariacionGananciaPorcentaje { get; set; }

        public decimal? VariacionServiciosPorcentaje { get; set; }

        public decimal? VariacionProductosPorcentaje { get; set; }

        public decimal? VariacionCitasOnlinePorcentaje { get; set; }

        // ─────────────── Mensajes interpretativos (reglas simples, sin IA) ───────────────

        public string ResumenEjecutivoTexto { get; set; } = string.Empty;

        public string ComentarioMargen { get; set; } = string.Empty;

        public string ComentarioActividad { get; set; } = string.Empty;

        public string ComentarioFuncionarioEstrella { get; set; } = string.Empty;

        public string ComentarioOportunidad { get; set; } = string.Empty;

        public string ComentarioComparativa { get; set; } = string.Empty;

        public string ComentarioServicioTop { get; set; } = string.Empty;

        public string ComentarioReservasOnline { get; set; } = string.Empty;

        // ─────────────── Secciones habilitadas (según configuración del tenant) ───────────────

        public bool IncluirDatosFinancieros { get; set; } = true;

        public bool IncluirDatosOperativos { get; set; } = true;

        public bool IncluirRecomendaciones { get; set; } = true;

        public bool IncluirComparativa { get; set; } = true;
    }
}
