using System.Text.Json.Serialization;

namespace LuxuryApp.Models.Platform.MissionControl
{
    /// <summary>Estado de una señal de salud. El orden define la severidad (mayor = peor).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SignalState
    {
        Disabled = 0,
        Ok = 1,
        Unknown = 2,
        Warning = 3,
        Critical = 4
    }

    /// <summary>
    /// Señal de salud con evidencia. DTO serializable (sin entidades EF) para que el
    /// snapshot pueda exponerse como JSON a monitoreo externo autenticado.
    /// </summary>
    public sealed class MissionControlSignalViewModel
    {
        public string Key { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public SignalState State { get; init; }

        /// <summary>Valor medido + umbral, legible ("Latencia 42 ms (umbral 500 ms)").</summary>
        public string Evidence { get; init; } = string.Empty;

        public DateTime? MeasuredAtUtc { get; init; }

        /// <summary>Enlace a la pantalla con el detalle, si existe.</summary>
        public string? LinkUrl { get; init; }

        public string BadgeClass => State switch
        {
            SignalState.Ok => "platform-badge-success",
            SignalState.Warning => "platform-badge-warning",
            SignalState.Critical => "platform-badge-danger",
            SignalState.Disabled => "platform-badge-dark",
            _ => "platform-badge-blue"
        };

        public string Icon => State switch
        {
            SignalState.Ok => "bi-check-circle",
            SignalState.Warning => "bi-exclamation-triangle",
            SignalState.Critical => "bi-x-octagon",
            SignalState.Disabled => "bi-slash-circle",
            _ => "bi-question-circle"
        };

        public string StateLabel => State switch
        {
            SignalState.Ok => "OK",
            SignalState.Warning => "Atención",
            SignalState.Critical => "Crítico",
            SignalState.Disabled => "Deshabilitado",
            _ => "Desconocido"
        };
    }

    /// <summary>Cola de trabajo: objetos en estado accionable con edad del más antiguo.</summary>
    public sealed class MissionControlQueueViewModel
    {
        public string Key { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public int Count { get; init; }

        /// <summary>Fecha del ítem más antiguo (o del que vence primero, según la cola).</summary>
        public DateTime? OldestItemUtc { get; init; }

        /// <summary>Pantalla existente donde se resuelve la cola.</summary>
        public string LinkUrl { get; init; } = string.Empty;

        /// <summary>True cuando la cola representa dinero en riesgo (se pinta con mayor severidad).</summary>
        public bool IsMoneyRelated { get; init; }

        public bool HasItems => Count > 0;
    }

    /// <summary>Pulso del día: detección de "silencio anómalo", no métricas de negocio.</summary>
    public sealed class MissionControlPulseViewModel
    {
        public int PagosConfirmadosHoy { get; init; }
        public int MensajesWhatsAppHoy { get; init; }
        public int ReservasRecibidasHoy { get; init; }
    }

    /// <summary>Fotografía completa del Mission Control (cacheada ~45 s).</summary>
    public sealed class MissionControlSnapshotViewModel
    {
        public DateTime GeneratedAtUtc { get; init; }
        public IReadOnlyList<MissionControlSignalViewModel> Signals { get; init; } = [];
        public IReadOnlyList<MissionControlQueueViewModel> Queues { get; init; } = [];
        public MissionControlPulseViewModel Pulse { get; init; } = new();

        /// <summary>Peor estado entre todas las señales (las colas no elevan el semáforo por sí solas).</summary>
        public SignalState OverallState =>
            Signals.Count == 0 ? SignalState.Unknown : Signals.Max(signal => signal.State);

        public int TotalQueueItems => Queues.Sum(queue => queue.Count);

        public bool IsAllClear =>
            OverallState is SignalState.Ok or SignalState.Disabled && TotalQueueItems == 0;
    }
}
