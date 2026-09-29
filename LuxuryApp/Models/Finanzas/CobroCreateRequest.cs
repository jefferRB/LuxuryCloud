namespace LuxuryApp.Models.Finanzas
{
    public sealed class CobroCreateRequest
    {
        public DateTime FechaCobro { get; init; }
        public string NombreCliente { get; init; } = string.Empty;
        public int? ClienteId { get; init; }
        public int FuncionarioId { get; init; }
        public int? ServicioId { get; init; }

        /// <summary>
        /// Nombre del servicio personalizado (cita fuera de catálogo). Solo válido cuando no hay
        /// <see cref="ServicioId"/> ni <see cref="ProductoId"/> y el cobro proviene de una cita
        /// (<see cref="CitaId"/>). Permite cobrar citas con servicio personalizado sin precio base.
        /// </summary>
        public string? ServicioNombrePersonalizado { get; init; }

        public int? ProductoId { get; init; }
        public decimal Monto { get; init; }
        public string MetodoPago { get; init; } = string.Empty;
        public string? Observaciones { get; init; }
        public bool ActualizarNotasServicio { get; init; }
        public string? NotasServicioTexto { get; init; }

        /// <summary>
        /// Cita de origen (opcional). Cuando viene del portal del funcionario, asocia el cobro
        /// a la cita para impedir doble cobro. El servicio valida pertenencia y unicidad.
        /// </summary>
        public int? CitaId { get; init; }

        // ─────────────── Comprobante digital interno (opcional) ───────────────

        /// <summary>Si es true, tras registrar el cobro se genera y envía un comprobante por correo.</summary>
        public bool EnviarComprobante { get; init; }

        /// <summary>Correo destino del comprobante. Obligatorio y válido solo si <see cref="EnviarComprobante"/>.</summary>
        public string? EmailComprobante { get; init; }

        /// <summary>Si es true y el cobro está ligado a un cliente, guarda el correo en su perfil.</summary>
        public bool GuardarEmailEnCliente { get; init; }
    }
}
