using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Common;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Reservas
{
    /// <summary>
    /// Solicitud de reserva enviada por un cliente desde el link público. En Fase 1 NO crea
    /// una cita: queda en estado Pending hasta que el negocio la confirma o la rechaza desde
    /// la plataforma privada. Al confirmar se crea la cita real y se guarda <see cref="ConvertedCitaId"/>.
    /// </summary>
    public sealed class BookingRequest : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        public int Id { get; set; }

        public int ServicioId { get; set; }
        public Servicio? Servicio { get; set; }

        /// <summary>
        /// Funcionario que pidió EL CLIENTE. Null = "cualquier funcionario disponible".
        /// Es la intención del cliente y NO se reescribe nunca: al confirmar se conserva tal cual.
        /// </summary>
        public int? FuncionarioId { get; set; }
        public Funcionario? Funcionario { get; set; }

        /// <summary>
        /// Funcionario que EL SERVIDOR reservó para esta solicitud. Se fija en el momento de
        /// aceptar la solicitud (incluso cuando el cliente pidió "cualquiera") porque una
        /// solicitud Pending ocupa agenda: sin un recurso concreto no se sabría qué agenda
        /// bloquear, y bloquear a todos sería incorrecto.
        ///
        /// <para>
        /// Null solo en solicitudes anteriores a esta función. Para esas se cae de vuelta a
        /// <see cref="FuncionarioId"/> (ver <see cref="FuncionarioReservadoId"/>), de modo que el
        /// histórico conserva exactamente el comportamiento con el que se creó.
        /// </para>
        /// </summary>
        public int? FuncionarioAsignadoId { get; set; }
        public Funcionario? FuncionarioAsignado { get; set; }

        /// <summary>
        /// Agenda que ocupa la solicitud: el asignado por el servidor y, para filas antiguas, el
        /// solicitado por el cliente. Null = no ocupa ninguna agenda concreta (legado "cualquiera").
        /// En consultas LINQ-a-SQL hay que escribir la coalescencia a mano: esta propiedad no se mapea.
        /// </summary>
        [NotMapped]
        public int? FuncionarioReservadoId => FuncionarioAsignadoId ?? FuncionarioId;

        /// <summary>Cliente existente asociado (resuelto por teléfono dentro del tenant), si lo hay.</summary>
        public int? ClienteId { get; set; }
        public ClientesModel? Cliente { get; set; }

        [Required]
        [MaxLength(100)]
        public string NombreCliente { get; set; } = string.Empty;

        [Required]
        [MaxLength(30)]
        public string TelefonoCliente { get; set; } = string.Empty;

        [MaxLength(256)]
        public string? CorreoCliente { get; set; }

        /// <summary>Inicio solicitado en hora local del negocio (mismo criterio que Cita.FechaHoraCita).</summary>
        public DateTime FechaHoraInicioSolicitada { get; set; }

        public DateTime FechaHoraFinCalculada { get; set; }

        public int DuracionMinutos { get; set; }

        [MaxLength(30)]
        public string Estado { get; set; } = BookingRequestStates.Pending;

        [MaxLength(500)]
        public string? NotasCliente { get; set; }

        [MaxLength(40)]
        public string Origen { get; set; } = BookingRequestOrigins.PublicLink;

        /// <summary>Si el cliente autorizó recibir la confirmación por WhatsApp.</summary>
        public bool AceptaWhatsApp { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime? ConfirmedAtUtc { get; set; }

        [MaxLength(450)]
        public string? ConfirmedByUserId { get; set; }

        public DateTime? RejectedAtUtc { get; set; }

        [MaxLength(450)]
        public string? RejectedByUserId { get; set; }

        [MaxLength(300)]
        public string? RejectedReason { get; set; }

        /// <summary>Cita creada al confirmar. SetNull si la cita se elimina luego.</summary>
        public int? ConvertedCitaId { get; set; }
        public Cita? ConvertedCita { get; set; }

        /// <summary>
        /// Token único generado por cada carga del formulario público. Idempotencia: dos envíos con
        /// el mismo token (doble click, reintento del navegador, JS duplicado) NO crean dos solicitudes.
        /// Índice único filtrado por TenantId + token (cuando no es null).
        /// </summary>
        [MaxLength(64)]
        public string? PublicSubmissionToken { get; set; }

        /// <summary>Hash de IP (no se guarda la IP en claro) para anti-spam/auditoría ligera.</summary>
        [MaxLength(64)]
        public string? IpHash { get; set; }

        [MaxLength(400)]
        public string? UserAgent { get; set; }
    }
}
