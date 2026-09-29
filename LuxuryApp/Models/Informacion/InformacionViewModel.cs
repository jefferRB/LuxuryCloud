namespace LuxuryApp.Models.Informacion
{
    public class InformacionViewModel
    {
        public int MesSeleccionado { get; set; }
        public int AnioSeleccionado { get; set; }

        // KPIs

        public string MesMasCitas { get; set; } = string.Empty;
        public int TotalMesMasCitas { get; set; }

        public string MesMenosCitas { get; set; } = string.Empty;
        public int TotalMesMenosCitas { get; set; }

        public string DiaMasOcupado { get; set; } = string.Empty;
        public int TotalDiaMasOcupado { get; set; }

        public string DiaMasLibre { get; set; } = string.Empty;
        public int TotalDiaMasLibre { get; set; }

        public string HoraMasOcupada { get; set; } = string.Empty;
        public double PromedioHoraMasOcupada { get; set; }

        public string HoraMasLibre { get; set; } = string.Empty;
        public double PromedioHoraMasLibre { get; set; }

        public string ServicioMasSolicitado { get; set; } = string.Empty;
        public int TotalServicioMasSolicitado { get; set; }

        public List<string> ServiciosNombres { get; set; } = new();
        public List<int> ServiciosCantidad { get; set; } = new();

        public List<string> ProductosVendidosNombres { get; set; } = new();
        public List<int> ProductosVendidosCantidad { get; set; } = new();

        public string ProductoMasVendido { get; set; } = string.Empty;
        public int TotalProductoMasVendido { get; set; }
        public string ProductoMenosVendido { get; set; } = string.Empty;
        public int TotalProductoMenosVendido { get; set; }

        public string FuncionarioMasCitas { get; set; } = string.Empty;
        public int TotalFuncionarioCitas { get; set; }

        // KPIs mensuales calculados
        public int CantidadServiciosMes { get; set; }
        public int CantidadProductosMes { get; set; }
        public int ReservasOnlineMes { get; set; }

        // GRAFICOS

        public List<int> CitasPorMes { get; set; } = new();
        public List<int> ProductosVendidosPorMes { get; set; } = new();

        public List<string> SemanaDias { get; set; } = new();
        public List<int> SemanaCitas { get; set; } = new();

        public List<string> FuncionariosNombres { get; set; } = new();
        public List<int> FuncionariosCitas { get; set; } = new();

        // TOP CLIENTES

        public int TopCantidad { get; set; } = 10;
        public List<TopClienteVM> TopClientes { get; set; } = new();
    }

    public class TopClienteVM
    {
        public string Nombre { get; set; } = string.Empty;
        public string Telefono { get; set; } = string.Empty;
        public int TotalVisitas { get; set; }
    }
}
