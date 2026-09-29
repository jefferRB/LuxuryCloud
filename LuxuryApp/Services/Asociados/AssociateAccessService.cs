using LuxuryApp.Models.Asociados;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Platform;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Asociados
{
    /// <summary>
    /// Acceso al sistema de los asociados.
    /// Ver <see cref="IAssociateAccessService"/> para el contrato y las reglas.
    /// </summary>
    public sealed class AssociateAccessService : IAssociateAccessService
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<AppUsuario> _userManager;
        private readonly ITenantAccountProvisioningService _accountProvisioning;
        private readonly IAssociatePermissionService _permissionService;
        private readonly IPlatformAuditService _auditService;
        private readonly ILogger<AssociateAccessService> _logger;

        public AssociateAccessService(
            ApplicationDbContext context,
            UserManager<AppUsuario> userManager,
            ITenantAccountProvisioningService accountProvisioning,
            IAssociatePermissionService permissionService,
            IPlatformAuditService auditService,
            ILogger<AssociateAccessService> logger)
        {
            _context = context;
            _userManager = userManager;
            _accountProvisioning = accountProvisioning;
            _permissionService = permissionService;
            _auditService = auditService;
            _logger = logger;
        }

        public async Task<AssociateAccessViewModel> ObtenerEstadoAsync(
            int associateId,
            CancellationToken cancellationToken = default)
        {
            // Tenant-safe por el filtro global.
            var associate = await _context.Associates
                .AsNoTracking()
                .Where(current => current.Id == associateId)
                .Select(current => new { current.Id, current.Nombre, current.Activo, current.AppUsuarioId, current.Email })
                .FirstOrDefaultAsync(cancellationToken);

            if (associate is null)
            {
                return new AssociateAccessViewModel
                {
                    AssociateId = associateId,
                    Estado = AssociateAccessState.SinAcceso
                };
            }

            if (string.IsNullOrWhiteSpace(associate.AppUsuarioId))
            {
                return new AssociateAccessViewModel
                {
                    AssociateId = associate.Id,
                    Nombre = associate.Nombre,
                    AsociadoActivo = associate.Activo,
                    Estado = AssociateAccessState.SinAcceso,
                    Email = associate.Email
                };
            }

            var cuenta = await _context.Users
                .AsNoTracking()
                .Where(user => user.Id == associate.AppUsuarioId)
                .Select(user => new { user.Email, user.State })
                .FirstOrDefaultAsync(cancellationToken);

            var estado = cuenta is null
                ? AssociateAccessState.SinAcceso
                : cuenta.State
                    ? AssociateAccessState.AccesoActivo
                    : AssociateAccessState.AccesoBloqueado;

            return new AssociateAccessViewModel
            {
                AssociateId = associate.Id,
                Nombre = associate.Nombre,
                AsociadoActivo = associate.Activo,
                Estado = estado,
                Email = cuenta?.Email ?? associate.Email
            };
        }

        public async Task<AssociateAccessResult> ActivarAccesoAsync(
            int associateId,
            string email,
            AssociateCredentialMode modo,
            string? contrasenaTemporal,
            IEnumerable<string> permisosIniciales,
            string? actorUserId,
            CancellationToken cancellationToken = default)
        {
            email = (email ?? string.Empty).Trim();

            if (!_accountProvisioning.EsEmailValido(email))
            {
                return AssociateAccessResult.Falla("Ingresa un correo electrónico válido.");
            }

            var associate = await _context.Associates
                .FirstOrDefaultAsync(current => current.Id == associateId, cancellationToken);

            if (associate is null)
            {
                return AssociateAccessResult.Falla("El asociado no existe o no pertenece a tu negocio.");
            }

            if (!associate.Activo)
            {
                return AssociateAccessResult.Falla(
                    "No puedes habilitar acceso a un asociado inactivo. Actívalo primero.");
            }

            if (!string.IsNullOrWhiteSpace(associate.AppUsuarioId))
            {
                return AssociateAccessResult.Falla(
                    "Este asociado ya tiene una cuenta de acceso. Usa reenviar invitación o reactivar acceso.");
            }

            if (await _accountProvisioning.CorreoEnUsoAsync(email))
            {
                return AssociateAccessResult.Falla(
                    "Ese correo ya está registrado en LuxuryCloud. Usa un correo diferente para el asociado.");
            }

            var request = new TenantAccountRequest
            {
                Email = email,
                DisplayName = associate.Nombre,
                Telefono = associate.Telefono,
                TenantId = associate.TenantId,
                Role = AppRoles.Asociado,
                Modo = modo == AssociateCredentialMode.ContrasenaTemporal
                    ? TenantAccountCredentialMode.ContrasenaTemporal
                    : TenantAccountCredentialMode.Invitacion,
                ContrasenaTemporal = contrasenaTemporal
            };

            var resultado = await _accountProvisioning.CrearCuentaAsync(
                request,
                async (usuario, ct) =>
                {
                    associate.AppUsuarioId = usuario.Id;

                    // El correo de acceso pasa a ser el correo del asociado: una sola verdad.
                    associate.Email = email;
                    associate.UpdatedAtUtc = DateTime.UtcNow;
                    associate.UpdatedByUserId = actorUserId;

                    await _context.SaveChangesAsync(ct);
                    return true;
                },
                cancellationToken);

            if (!resultado.Exitoso)
            {
                return AssociateAccessResult.Falla(resultado.Errores.ToArray());
            }

            // Permisos explícitos desde el primer segundo: un asociado sin concesiones no ve nada,
            // así que la cuenta nunca queda "activa pero inútil" ni "activa con más de lo pedido".
            await _permissionService.GuardarAsync(associateId, permisosIniciales, actorUserId, cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.AssociateAccessGranted,
                    EntityType = PlatformAuditEntityTypes.Associate,
                    EntityId = associate.Id.ToString(),
                    TenantId = associate.TenantId,
                    TargetUserId = resultado.UserId,
                    TargetUserEmail = email,
                    AfterJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Modo = modo.ToString(),
                        associate.Nombre
                    })
                },
                cancellationToken);

            _logger.LogInformation(
                "Acceso de asociado habilitado. TenantId {TenantId}. AssociateId {AssociateId}. UserId {UserId}. Modo {Modo}.",
                associate.TenantId,
                associate.Id,
                resultado.UserId,
                modo);

            return new AssociateAccessResult
            {
                Exitoso = true,
                UserId = resultado.UserId,
                Email = resultado.Email,
                NombreParaCorreo = associate.Nombre,
                EnlaceTokenCodificado = resultado.EnlaceTokenCodificado,
                RequiereCorreoInvitacion = modo == AssociateCredentialMode.Invitacion
            };
        }

        public async Task<AssociateAccessResult> BloquearAccesoAsync(
            int associateId,
            string? actorUserId,
            CancellationToken cancellationToken = default)
        {
            var (associate, usuario, error) = await ResolverCuentaAsync(associateId, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            var resultado = await _accountProvisioning.BloquearAsync(usuario!, cancellationToken);
            if (!resultado.Exitoso)
            {
                return AssociateAccessResult.Falla(resultado.Errores.ToArray());
            }

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.AssociateAccessBlocked,
                    EntityType = PlatformAuditEntityTypes.Associate,
                    EntityId = associate!.Id.ToString(),
                    TenantId = associate.TenantId,
                    TargetUserId = usuario!.Id,
                    TargetUserEmail = usuario.Email
                },
                cancellationToken);

            _logger.LogInformation(
                "Acceso de asociado bloqueado. AssociateId {AssociateId}. UserId {UserId}.",
                associate.Id,
                usuario.Id);

            return new AssociateAccessResult { Exitoso = true, UserId = usuario.Id, Email = usuario.Email };
        }

        public async Task<AssociateAccessResult> ReactivarAccesoAsync(
            int associateId,
            string? actorUserId,
            CancellationToken cancellationToken = default)
        {
            var (associate, usuario, error) = await ResolverCuentaAsync(associateId, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            if (!associate!.Activo)
            {
                return AssociateAccessResult.Falla(
                    "No puedes reactivar el acceso de un asociado inactivo. Actívalo primero.");
            }

            var resultado = await _accountProvisioning.ReactivarAsync(usuario!, cancellationToken);
            if (!resultado.Exitoso)
            {
                return AssociateAccessResult.Falla(resultado.Errores.ToArray());
            }

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.AssociateAccessRestored,
                    EntityType = PlatformAuditEntityTypes.Associate,
                    EntityId = associate.Id.ToString(),
                    TenantId = associate.TenantId,
                    TargetUserId = usuario!.Id,
                    TargetUserEmail = usuario.Email
                },
                cancellationToken);

            return new AssociateAccessResult { Exitoso = true, UserId = usuario.Id, Email = usuario.Email };
        }

        public async Task<AssociateAccessResult> GenerarEnlaceInvitacionAsync(
            int associateId,
            string? actorUserId,
            CancellationToken cancellationToken = default)
        {
            var (associate, usuario, error) = await ResolverCuentaAsync(associateId, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            var tokenCodificado = await _accountProvisioning.GenerarTokenContrasenaAsync(usuario!);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.AssociateAccessInvitationResent,
                    EntityType = PlatformAuditEntityTypes.Associate,
                    EntityId = associate!.Id.ToString(),
                    TenantId = associate.TenantId,
                    TargetUserId = usuario!.Id,
                    TargetUserEmail = usuario.Email
                },
                cancellationToken);

            return new AssociateAccessResult
            {
                Exitoso = true,
                UserId = usuario.Id,
                Email = usuario.Email,
                NombreParaCorreo = associate.Nombre,
                EnlaceTokenCodificado = tokenCodificado,
                RequiereCorreoInvitacion = true
            };
        }

        public async Task<AssociateAccessResult> CambiarCorreoAsync(
            int associateId,
            string nuevoEmail,
            string? actorUserId,
            CancellationToken cancellationToken = default)
        {
            nuevoEmail = (nuevoEmail ?? string.Empty).Trim();

            if (!_accountProvisioning.EsEmailValido(nuevoEmail))
            {
                return AssociateAccessResult.Falla("Ingresa un correo electrónico válido.");
            }

            var (associate, usuario, error) = await ResolverCuentaAsync(associateId, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            var anterior = usuario!.Email;

            var resultado = await _accountProvisioning.CambiarCorreoAsync(usuario, nuevoEmail, cancellationToken);
            if (!resultado.Exitoso)
            {
                return AssociateAccessResult.Falla(resultado.Errores.ToArray());
            }

            // El asociado y su cuenta comparten correo: si no se sincroniza, los estados de cuenta
            // y las invitaciones saldrían a direcciones distintas.
            var seguimiento = await _context.Associates
                .FirstOrDefaultAsync(current => current.Id == associateId, cancellationToken);

            if (seguimiento is not null)
            {
                seguimiento.Email = nuevoEmail;
                seguimiento.UpdatedAtUtc = DateTime.UtcNow;
                seguimiento.UpdatedByUserId = actorUserId;
                await _context.SaveChangesAsync(cancellationToken);
            }

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.AssociateAccessEmailChanged,
                    EntityType = PlatformAuditEntityTypes.Associate,
                    EntityId = associate!.Id.ToString(),
                    TenantId = associate.TenantId,
                    TargetUserId = usuario.Id,
                    TargetUserEmail = nuevoEmail,
                    BeforeJson = System.Text.Json.JsonSerializer.Serialize(new { Email = anterior }),
                    AfterJson = System.Text.Json.JsonSerializer.Serialize(new { Email = nuevoEmail })
                },
                cancellationToken);

            return new AssociateAccessResult
            {
                Exitoso = true,
                UserId = usuario.Id,
                Email = nuevoEmail,
                NombreParaCorreo = associate.Nombre
            };
        }

        private async Task<(Associate? Associate, AppUsuario? Usuario, AssociateAccessResult? Error)>
            ResolverCuentaAsync(int associateId, CancellationToken cancellationToken)
        {
            var associate = await _context.Associates
                .FirstOrDefaultAsync(current => current.Id == associateId, cancellationToken);

            if (associate is null)
            {
                return (null, null,
                    AssociateAccessResult.Falla("El asociado no existe o no pertenece a tu negocio."));
            }

            if (string.IsNullOrWhiteSpace(associate.AppUsuarioId))
            {
                return (associate, null,
                    AssociateAccessResult.Falla("Este asociado no tiene una cuenta de acceso."));
            }

            var usuario = await _userManager.FindByIdAsync(associate.AppUsuarioId);
            if (usuario is null)
            {
                return (associate, null,
                    AssociateAccessResult.Falla("No se encontró la cuenta de acceso del asociado."));
            }

            // Defensa en profundidad: asociar una cuenta de otro negocio jamás debe dar acceso.
            if (usuario.TenantId != associate.TenantId)
            {
                _logger.LogWarning(
                    "Desalineación tenant entre asociado y cuenta. AssociateId {AssociateId}. UserId {UserId}.",
                    associate.Id,
                    usuario.Id);

                return (associate, null,
                    AssociateAccessResult.Falla("La cuenta de acceso no es válida para este negocio."));
            }

            return (associate, usuario, null);
        }
    }
}
