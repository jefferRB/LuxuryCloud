using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Identity;
using LuxuryApp.Services.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Funcionarios
{
    /// <summary>
    /// Acceso al portal de los funcionarios.
    ///
    /// <para>
    /// La mecánica de Identity (crear la cuenta, asignar rol, transacción, bloquear, reactivar,
    /// tokens de contraseña, cambio de correo) NO vive acá: la aporta
    /// <see cref="ITenantAccountProvisioningService"/>, compartida con el módulo de Asociados.
    /// Este servicio solo pone las reglas propias del funcionario y el enlace con su ficha.
    /// </para>
    /// </summary>
    public sealed class FuncionarioPortalAccessService : IFuncionarioPortalAccessService
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<AppUsuario> _userManager;
        private readonly ITenantAccountProvisioningService _accountProvisioning;
        private readonly IFuncionarioPortalPermissionService _permissionService;
        private readonly ILogger<FuncionarioPortalAccessService> _logger;

        public FuncionarioPortalAccessService(
            ApplicationDbContext context,
            UserManager<AppUsuario> userManager,
            ITenantAccountProvisioningService accountProvisioning,
            IFuncionarioPortalPermissionService permissionService,
            ILogger<FuncionarioPortalAccessService> logger)
        {
            _context = context;
            _userManager = userManager;
            _accountProvisioning = accountProvisioning;
            _permissionService = permissionService;
            _logger = logger;
        }

        public async Task<FuncionarioAccesoViewModel> ObtenerEstadoAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default)
        {
            // Consulta tenant-safe por el global query filter.
            var funcionario = await _context.Funcionarios
                .AsNoTracking()
                .Where(f => f.IdFuncionario == funcionarioId)
                .Select(f => new { f.IdFuncionario, f.Nombre, f.Activo, f.AppUsuarioId })
                .FirstOrDefaultAsync(cancellationToken);

            if (funcionario is null)
            {
                return new FuncionarioAccesoViewModel
                {
                    FuncionarioId = funcionarioId,
                    Estado = FuncionarioAccesoEstado.SinAcceso
                };
            }

            if (string.IsNullOrWhiteSpace(funcionario.AppUsuarioId))
            {
                return new FuncionarioAccesoViewModel
                {
                    FuncionarioId = funcionario.IdFuncionario,
                    FuncionarioNombre = funcionario.Nombre,
                    FuncionarioActivo = funcionario.Activo,
                    Estado = FuncionarioAccesoEstado.SinAcceso
                };
            }

            var cuenta = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == funcionario.AppUsuarioId)
                .Select(u => new { u.Email, u.State })
                .FirstOrDefaultAsync(cancellationToken);

            var estado = cuenta is null
                ? FuncionarioAccesoEstado.SinAcceso
                : cuenta.State
                    ? FuncionarioAccesoEstado.AccesoActivo
                    : FuncionarioAccesoEstado.AccesoBloqueado;

            return new FuncionarioAccesoViewModel
            {
                FuncionarioId = funcionario.IdFuncionario,
                FuncionarioNombre = funcionario.Nombre,
                FuncionarioActivo = funcionario.Activo,
                Estado = estado,
                Email = cuenta?.Email
            };
        }

        public async Task<FuncionarioAccesoResultado> ActivarAccesoAsync(
            int funcionarioId,
            string email,
            FuncionarioAccesoCredencialModo modo,
            string? contrasenaTemporal,
            CancellationToken cancellationToken = default)
        {
            email = (email ?? string.Empty).Trim();

            if (!_accountProvisioning.EsEmailValido(email))
            {
                return FuncionarioAccesoResultado.Falla("Ingresa un correo electrónico válido.");
            }

            // Funcionario debe existir, pertenecer al tenant actual y estar activo.
            var funcionario = await _context.Funcionarios
                .FirstOrDefaultAsync(f => f.IdFuncionario == funcionarioId, cancellationToken);

            if (funcionario is null)
            {
                return FuncionarioAccesoResultado.Falla("El funcionario no existe o no pertenece a tu negocio.");
            }

            if (!funcionario.Activo)
            {
                return FuncionarioAccesoResultado.Falla(
                    "No puedes habilitar acceso a un funcionario inactivo. Actívalo primero.");
            }

            if (!string.IsNullOrWhiteSpace(funcionario.AppUsuarioId))
            {
                return FuncionarioAccesoResultado.Falla(
                    "Este funcionario ya tiene una cuenta de acceso. Usa reenviar invitación o reactivar acceso.");
            }

            if (await _accountProvisioning.CorreoEnUsoAsync(email))
            {
                return FuncionarioAccesoResultado.Falla(
                    "Ese correo ya está registrado en LuxuryCloud. Usa un correo diferente para el funcionario.");
            }

            var request = new TenantAccountRequest
            {
                Email = email,
                DisplayName = funcionario.Nombre,
                Telefono = funcionario.Telefono,
                TenantId = funcionario.TenantId,
                Role = AppRoles.Funcionario,
                Modo = modo == FuncionarioAccesoCredencialModo.ContrasenaTemporal
                    ? TenantAccountCredentialMode.ContrasenaTemporal
                    : TenantAccountCredentialMode.Invitacion,
                ContrasenaTemporal = contrasenaTemporal,
                Configure = usuario => usuario.FuncionarioId = funcionario.IdFuncionario
            };

            var resultado = await _accountProvisioning.CrearCuentaAsync(
                request,
                async (usuario, ct) =>
                {
                    funcionario.AppUsuarioId = usuario.Id;
                    await _context.SaveChangesAsync(ct);
                    return true;
                },
                cancellationToken);

            if (!resultado.Exitoso)
            {
                return FuncionarioAccesoResultado.Falla(resultado.Errores.ToArray());
            }

            // Permisos por defecto (solo lectura) al habilitar el acceso por primera vez.
            await _permissionService.CrearDefaultsAsync(funcionario.IdFuncionario, cancellationToken);

            _logger.LogInformation(
                "Acceso de funcionario habilitado. TenantId {TenantId}. FuncionarioId {FuncionarioId}. UserId {UserId}. Modo {Modo}.",
                funcionario.TenantId,
                funcionario.IdFuncionario,
                resultado.UserId,
                modo);

            return new FuncionarioAccesoResultado
            {
                Exitoso = true,
                UserId = resultado.UserId,
                Email = resultado.Email,
                NombreParaCorreo = funcionario.Nombre,
                EnlaceTokenCodificado = resultado.EnlaceTokenCodificado,
                RequiereCorreoInvitacion = modo == FuncionarioAccesoCredencialModo.Invitacion
            };
        }

        public async Task<FuncionarioAccesoResultado> DesactivarAccesoAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default)
        {
            var (funcionario, usuario, error) = await ResolverCuentaAsync(funcionarioId, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            var resultado = await _accountProvisioning.BloquearAsync(usuario!, cancellationToken);
            if (!resultado.Exitoso)
            {
                return FuncionarioAccesoResultado.Falla(resultado.Errores.ToArray());
            }

            _logger.LogInformation(
                "Acceso de funcionario desactivado. FuncionarioId {FuncionarioId}. UserId {UserId}.",
                funcionario!.IdFuncionario,
                usuario!.Id);

            return new FuncionarioAccesoResultado { Exitoso = true, UserId = usuario.Id, Email = usuario.Email };
        }

        public async Task<FuncionarioAccesoResultado> ReactivarAccesoAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default)
        {
            var (funcionario, usuario, error) = await ResolverCuentaAsync(funcionarioId, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            if (!funcionario!.Activo)
            {
                return FuncionarioAccesoResultado.Falla(
                    "No puedes reactivar el acceso de un funcionario inactivo. Actívalo primero.");
            }

            var resultado = await _accountProvisioning.ReactivarAsync(usuario!, cancellationToken);
            if (!resultado.Exitoso)
            {
                return FuncionarioAccesoResultado.Falla(resultado.Errores.ToArray());
            }

            _logger.LogInformation(
                "Acceso de funcionario reactivado. FuncionarioId {FuncionarioId}. UserId {UserId}.",
                funcionario.IdFuncionario,
                usuario!.Id);

            return new FuncionarioAccesoResultado { Exitoso = true, UserId = usuario.Id, Email = usuario.Email };
        }

        public async Task<FuncionarioAccesoResultado> GenerarEnlaceInvitacionAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default)
        {
            var (funcionario, usuario, error) = await ResolverCuentaAsync(funcionarioId, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            var tokenCodificado = await _accountProvisioning.GenerarTokenContrasenaAsync(usuario!);

            _logger.LogInformation(
                "Invitación/enlace de contraseña regenerado para funcionario. FuncionarioId {FuncionarioId}. UserId {UserId}.",
                funcionario!.IdFuncionario,
                usuario!.Id);

            return new FuncionarioAccesoResultado
            {
                Exitoso = true,
                UserId = usuario.Id,
                Email = usuario.Email,
                NombreParaCorreo = funcionario.Nombre,
                EnlaceTokenCodificado = tokenCodificado,
                RequiereCorreoInvitacion = true
            };
        }

        public async Task<FuncionarioAccesoResultado> CambiarCorreoAsync(
            int funcionarioId,
            string nuevoEmail,
            CancellationToken cancellationToken = default)
        {
            nuevoEmail = (nuevoEmail ?? string.Empty).Trim();

            if (!_accountProvisioning.EsEmailValido(nuevoEmail))
            {
                return FuncionarioAccesoResultado.Falla("Ingresa un correo electrónico válido.");
            }

            var (funcionario, usuario, error) = await ResolverCuentaAsync(funcionarioId, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            var resultado = await _accountProvisioning.CambiarCorreoAsync(usuario!, nuevoEmail, cancellationToken);
            if (!resultado.Exitoso)
            {
                return FuncionarioAccesoResultado.Falla(resultado.Errores.ToArray());
            }

            _logger.LogInformation(
                "Correo de acceso de funcionario actualizado. FuncionarioId {FuncionarioId}. UserId {UserId}.",
                funcionario!.IdFuncionario,
                usuario!.Id);

            return new FuncionarioAccesoResultado
            {
                Exitoso = true,
                UserId = usuario.Id,
                Email = resultado.Email,
                NombreParaCorreo = funcionario.Nombre
            };
        }

        private async Task<(Funcionario? Funcionario, AppUsuario? Usuario, FuncionarioAccesoResultado? Error)>
            ResolverCuentaAsync(int funcionarioId, CancellationToken cancellationToken)
        {
            var funcionario = await _context.Funcionarios
                .FirstOrDefaultAsync(f => f.IdFuncionario == funcionarioId, cancellationToken);

            if (funcionario is null)
            {
                return (null, null,
                    FuncionarioAccesoResultado.Falla("El funcionario no existe o no pertenece a tu negocio."));
            }

            if (string.IsNullOrWhiteSpace(funcionario.AppUsuarioId))
            {
                return (funcionario, null,
                    FuncionarioAccesoResultado.Falla("Este funcionario no tiene una cuenta de acceso."));
            }

            var usuario = await _userManager.FindByIdAsync(funcionario.AppUsuarioId);
            if (usuario is null)
            {
                return (funcionario, null,
                    FuncionarioAccesoResultado.Falla("No se encontró la cuenta de acceso del funcionario."));
            }

            // Defensa en profundidad: la cuenta debe pertenecer al mismo tenant.
            if (usuario.TenantId != funcionario.TenantId)
            {
                _logger.LogWarning(
                    "Desalineación tenant entre funcionario y cuenta. FuncionarioId {FuncionarioId}. UserId {UserId}.",
                    funcionario.IdFuncionario,
                    usuario.Id);
                return (funcionario, null,
                    FuncionarioAccesoResultado.Falla("La cuenta de acceso no es válida para este negocio."));
            }

            return (funcionario, usuario, null);
        }
    }
}
