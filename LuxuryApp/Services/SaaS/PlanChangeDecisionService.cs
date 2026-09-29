using LuxuryApp.Models.SaaS;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.SaaS
{
    /// <summary>Decisión sobre qué hacer cuando un tenant selecciona un plan base en la calculadora.</summary>
    public enum PlanChangeDecision
    {
        /// <summary>Sin suscripción base activa: compra normal (crea suscriptor nuevo).</summary>
        ProceedNormalCheckout,

        /// <summary>El plan elegido es el que ya tiene activo: no se cobra, UI muestra "plan actual".</summary>
        SamePlan,

        /// <summary>Cambio de plan válido: crear PlanChangeIntent y checkout del destino.</summary>
        ProceedPlanChange,

        /// <summary>Bloqueado: el destino tiene menos cupo que los funcionarios activos.</summary>
        BlockedFuncionarioLimit,

        /// <summary>Bloqueado: la suscripción activa no tiene ProviderSubscriptionId (no se puede cancelar el viejo).</summary>
        BlockedMissingProviderSubscription,

        /// <summary>Bloqueado: hay un cambio previo aplicado cuyo suscriptor viejo aún no se canceló (riesgo triple cobro).</summary>
        BlockedPendingOldCancellation,

        /// <summary>
        /// Bloqueado: la cancelación automática del suscriptor viejo está deshabilitada, así que un
        /// cambio pagado dejaría DOS suscriptores cobrando. Sin auto-cancelación no hay cambio automático.
        /// </summary>
        BlockedAutoCancellationDisabled
    }

    public sealed record PlanChangeEvaluation
    {
        public required PlanChangeDecision Decision { get; init; }

        /// <summary>Mensaje seguro para mostrar al usuario cuando la decisión bloquea o no cambia nada.</summary>
        public string? Message { get; init; }

        /// <summary>Suscripción base actual (para construir el PlanChangeRequest cuando procede el cambio).</summary>
        public Suscripcion? CurrentSubscription { get; init; }

        public bool IsBlocked =>
            Decision is PlanChangeDecision.BlockedFuncionarioLimit
                or PlanChangeDecision.BlockedMissingProviderSubscription
                or PlanChangeDecision.BlockedPendingOldCancellation
                or PlanChangeDecision.BlockedAutoCancellationDisabled;
    }

    public interface IPlanChangeDecisionService
    {
        /// <summary>
        /// Evalúa server-side, de forma determinística y testeable, si una selección de plan base
        /// es compra normal, mismo plan, cambio válido o un bloqueo. Centraliza las guardas de
        /// dinero recurrente para que el controller no las duplique.
        /// </summary>
        Task<PlanChangeEvaluation> EvaluateAsync(
            Guid tenantId,
            int targetTilopayRecurringPlanId,
            int targetWorkerCount,
            int activeFuncionarios,
            CancellationToken cancellationToken = default);
    }

    public sealed class PlanChangeDecisionService : IPlanChangeDecisionService
    {
        private readonly ApplicationDbContext _db;
        private readonly SuscripcionService _suscripcionService;
        private readonly OpcionesTilopayRepeatAdmin _adminOptions;

        public PlanChangeDecisionService(
            ApplicationDbContext db,
            SuscripcionService suscripcionService,
            Microsoft.Extensions.Options.IOptions<OpcionesTilopayRepeatAdmin>? adminOptions = null)
        {
            _db = db;
            _suscripcionService = suscripcionService;
            _adminOptions = adminOptions?.Value ?? new OpcionesTilopayRepeatAdmin();
        }

        public async Task<PlanChangeEvaluation> EvaluateAsync(
            Guid tenantId,
            int targetTilopayRecurringPlanId,
            int targetWorkerCount,
            int activeFuncionarios,
            CancellationToken cancellationToken = default)
        {
            var currentSubscription = await _db.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => s.TenantId == tenantId)
                .OrderByDescending(s => s.FechaUltimaActualizacionUtc ?? s.FechaInicio)
                .FirstOrDefaultAsync(cancellationToken);

            var hasActiveBase = currentSubscription is not null &&
                                currentSubscription.TilopayRecurringPlanId.HasValue &&
                                _suscripcionService.CanAccessApp(currentSubscription);

            // Piso de funcionarios (aplica a compra y a cambio): no permitir un plan con menos cupo
            // que los funcionarios activos. Es la MISMA definición de cupo que FuncionariosController.
            if (targetWorkerCount < activeFuncionarios)
            {
                return new PlanChangeEvaluation
                {
                    Decision = PlanChangeDecision.BlockedFuncionarioLimit,
                    CurrentSubscription = currentSubscription,
                    Message = hasActiveBase
                        ? $"Para bajar a este plan, primero desactivá funcionarios hasta quedar en {targetWorkerCount} o menos."
                        : $"Tu negocio tiene {activeFuncionarios} funcionarios activos. Elegí un plan para al menos {activeFuncionarios}."
                };
            }

            if (!hasActiveBase)
            {
                return new PlanChangeEvaluation { Decision = PlanChangeDecision.ProceedNormalCheckout };
            }

            // Mismo plan que el activo: no se genera checkout.
            if (currentSubscription!.TilopayRecurringPlanId!.Value == targetTilopayRecurringPlanId)
            {
                return new PlanChangeEvaluation
                {
                    Decision = PlanChangeDecision.SamePlan,
                    CurrentSubscription = currentSubscription,
                    Message = "Ese ya es tu plan actual."
                };
            }

            // Sin cancelación automática NO hay cambio automático: si dejáramos pagar el plan nuevo,
            // el viejo seguiría cobrando y el cliente quedaría con dos rebajos. Fail-closed.
            if (!_adminOptions.Enabled || !_adminOptions.AutoCancelOldSubscriberOnUpgrade)
            {
                return new PlanChangeEvaluation
                {
                    Decision = PlanChangeDecision.BlockedAutoCancellationDisabled,
                    CurrentSubscription = currentSubscription,
                    Message = "Para cambiar de plan, contactá soporte. El cambio automático estará disponible pronto."
                };
            }

            // Para cambiar DEBEMOS poder cancelar el suscriptor viejo tras confirmar el nuevo pago.
            if (string.IsNullOrWhiteSpace(currentSubscription.ProviderSubscriptionId))
            {
                return new PlanChangeEvaluation
                {
                    Decision = PlanChangeDecision.BlockedMissingProviderSubscription,
                    CurrentSubscription = currentSubscription,
                    Message = "Estamos verificando tu suscripción actual antes de permitir el cambio de plan. Soporte fue notificado; intentá más tarde o contactanos."
                };
            }

            // Riesgo triple cobro: hay un cambio PREVIO ya aplicado cuyo suscriptor viejo NO se canceló.
            // Si permitimos otro cambio, quedarían múltiples suscriptores rebajando en TiloPay.
            var hasUnresolvedOldCancellation = await _db.PlanChangeIntents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(
                    intent =>
                        intent.TenantId == tenantId &&
                        intent.Estado == PlanChangeIntentState.Applied &&
                        intent.OldProviderCancellation == ProviderCancellationState.PendingManualCancellation,
                    cancellationToken);

            if (hasUnresolvedOldCancellation)
            {
                return new PlanChangeEvaluation
                {
                    Decision = PlanChangeDecision.BlockedPendingOldCancellation,
                    CurrentSubscription = currentSubscription,
                    Message = "Tenemos un cambio de plan anterior en verificación. Esperá a que se complete antes de iniciar otro; soporte ya fue notificado."
                };
            }

            return new PlanChangeEvaluation
            {
                Decision = PlanChangeDecision.ProceedPlanChange,
                CurrentSubscription = currentSubscription
            };
        }
    }
}
