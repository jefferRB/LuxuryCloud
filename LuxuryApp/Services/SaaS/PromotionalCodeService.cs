using System.Data;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.SaaS
{
    public sealed class PromotionalCodeService : IPromotionalCodeService
    {
        private readonly ApplicationDbContext _context;
        private readonly ITenantCommercialAccessCache _accessCache;

        public PromotionalCodeService(
            ApplicationDbContext context,
            ITenantCommercialAccessCache accessCache)
        {
            _context = context;
            _accessCache = accessCache;
        }

        public async Task<PromotionalCodeRedemptionResult> RedeemAsync(
            string code,
            Guid tenantId,
            AppUsuario user,
            CancellationToken cancellationToken = default)
        {
            if (tenantId == Guid.Empty)
            {
                return PromotionalCodeRedemptionResult.Failure("No fue posible resolver el tenant actual.");
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return PromotionalCodeRedemptionResult.Failure("Debes ingresar un código promocional válido.");
            }

            if (string.IsNullOrWhiteSpace(user.Email))
            {
                return PromotionalCodeRedemptionResult.Failure("Tu usuario no tiene correo asociado para validar el código.");
            }

            var normalizedCode = NormalizeCode(code);
            var ownTransaction = _context.Database.CurrentTransaction is null;

            await using var transaction = ownTransaction
                ? await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                : null;

            var promotionalCode = await _context.Set<PromotionalCode>()
                .Include(c => c.Plan)
                .FirstOrDefaultAsync(c => c.Codigo == normalizedCode, cancellationToken);

            if (promotionalCode is null)
            {
                return PromotionalCodeRedemptionResult.Failure("El código indicado no existe.");
            }

            if (!promotionalCode.Activo)
            {
                return PromotionalCodeRedemptionResult.Failure("El código promocional no está activo.");
            }

            if (promotionalCode.FechaExpiracionUtc.HasValue && promotionalCode.FechaExpiracionUtc.Value < DateTime.UtcNow)
            {
                return PromotionalCodeRedemptionResult.Failure("El código promocional ya expiró.");
            }

            if (promotionalCode.Plan is null || !promotionalCode.Plan.Activo)
            {
                return PromotionalCodeRedemptionResult.Failure("El código no tiene un plan válido asociado.");
            }

            if (!string.IsNullOrWhiteSpace(promotionalCode.EmailObjetivo) &&
                !string.Equals(
                    promotionalCode.EmailObjetivo.Trim(),
                    user.Email.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return PromotionalCodeRedemptionResult.Failure("El código no fue emitido para este correo.");
            }

            if (promotionalCode.MaxUsos.HasValue && promotionalCode.UsosActuales >= promotionalCode.MaxUsos.Value)
            {
                return PromotionalCodeRedemptionResult.Failure("El código ya alcanzó su número máximo de usos.");
            }

            var alreadyRedeemedByTenant = await _context.Set<PromotionalCodeRedemption>()
                .IgnoreQueryFilters()
                .AnyAsync(
                    redemption => redemption.PromotionalCodeId == promotionalCode.Id && redemption.TenantId == tenantId,
                    cancellationToken);

            if (alreadyRedeemedByTenant)
            {
                return PromotionalCodeRedemptionResult.Failure("Este tenant ya consumió ese código promocional.");
            }

            var hasActiveGrant = await _context.Set<TenantCommercialAccessGrant>()
                .IgnoreQueryFilters()
                .AnyAsync(
                    grant => grant.TenantId == tenantId &&
                             grant.Activo &&
                             grant.FechaInicioUtc <= DateTime.UtcNow &&
                             grant.FechaFinUtc >= DateTime.UtcNow,
                    cancellationToken);

            if (hasActiveGrant)
            {
                return PromotionalCodeRedemptionResult.Failure("El tenant ya tiene un acceso promocional temporal activo.");
            }

            if (promotionalCode.SoloPrimerRegistro)
            {
                var userCount = await _context.Users
                    .IgnoreQueryFilters()
                    .CountAsync(currentUser => currentUser.TenantId == tenantId, cancellationToken);

                if (userCount > 1)
                {
                    return PromotionalCodeRedemptionResult.Failure("El código solo puede usarse durante el primer registro del tenant.");
                }
            }

            var grant = new TenantCommercialAccessGrant
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = promotionalCode.PlanId,
                Source = TenantCommercialAccessGrantSource.PromotionalCode,
                RequiresBilling = false,
                FechaInicioUtc = DateTime.UtcNow,
                FechaFinUtc = DateTime.UtcNow.AddDays(promotionalCode.DiasGratis),
                PromotionalCodeId = promotionalCode.Id,
                CreadoPorUserId = user.Id,
                NotasInternas = $"Código promocional {promotionalCode.Codigo} consumido por {user.Email}."
            };

            var redemption = new PromotionalCodeRedemption
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PromotionalCodeId = promotionalCode.Id,
                TenantCommercialAccessGrantId = grant.Id,
                ConsumidoPorUserId = user.Id,
                EmailConsumidor = user.Email.Trim(),
                FechaConsumoUtc = DateTime.UtcNow
            };

            promotionalCode.UsosActuales += 1;
            promotionalCode.FechaActualizacionUtc = DateTime.UtcNow;

            _context.Set<TenantCommercialAccessGrant>().Add(grant);
            _context.Set<PromotionalCodeRedemption>().Add(redemption);
            await _context.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            _accessCache.Invalidate(tenantId);
            return PromotionalCodeRedemptionResult.Success(promotionalCode, grant);
        }

        private static string NormalizeCode(string value) =>
            value.Trim().ToUpperInvariant();
    }
}
