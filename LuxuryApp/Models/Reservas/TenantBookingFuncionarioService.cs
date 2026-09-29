using LuxuryApp.Models.Common;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Reservas
{
    /// <summary>
    /// Relación configurable "qué funcionario puede atender qué servicio" en reservas online.
    /// Índice único por TenantId + FuncionarioId + ServicioId. Compatibilidad: si un servicio no
    /// tiene NINGÚN funcionario configurado (o ninguno habilitado), se asume que todos los
    /// funcionarios activos pueden atenderlo. Si tiene al menos uno habilitado, solo esos cuentan.
    /// </summary>
    public sealed class TenantBookingFuncionarioService : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        public int Id { get; set; }

        public int FuncionarioId { get; set; }
        public Funcionario? Funcionario { get; set; }

        public int ServicioId { get; set; }
        public Servicio? Servicio { get; set; }

        /// <summary>Si el funcionario está habilitado para atender este servicio online.</summary>
        public bool IsEnabled { get; set; } = true;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
