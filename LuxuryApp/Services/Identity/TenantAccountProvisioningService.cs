using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LuxuryApp.Models.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Identity
{
    /// <summary>
    /// Implementación única de la provisión de cuentas de acceso del tenant.
    /// Ver <see cref="ITenantAccountProvisioningService"/> para el contrato y el porqué.
    /// </summary>
    public sealed class TenantAccountProvisioningService : ITenantAccountProvisioningService
    {
        private static readonly Regex EmailRegex = new(
            @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly ApplicationDbContext _context;
        private readonly UserManager<AppUsuario> _userManager;
        private readonly ILogger<TenantAccountProvisioningService> _logger;

        public TenantAccountProvisioningService(
            ApplicationDbContext context,
            UserManager<AppUsuario> userManager,
            ILogger<TenantAccountProvisioningService> logger)
        {
            _context = context;
            _userManager = userManager;
            _logger = logger;
        }

        public bool EsEmailValido(string? email) =>
            !string.IsNullOrWhiteSpace(email) && EmailRegex.IsMatch(email.Trim());

        public async Task<bool> CorreoEnUsoAsync(string email, string? excluirUserId = null)
        {
            var existente = await _userManager.FindByEmailAsync(email.Trim());
            return existente is not null &&
                   !string.Equals(existente.Id, excluirUserId, StringComparison.Ordinal);
        }

        public async Task<TenantAccountResult> CrearCuentaAsync(
            TenantAccountRequest request,
            Func<AppUsuario, CancellationToken, Task<bool>> onCreatedInTransaction,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(onCreatedInTransaction);

            var email = (request.Email ?? string.Empty).Trim();

            if (!EsEmailValido(email))
            {
                return TenantAccountResult.Falla("Ingresa un correo electrónico válido.");
            }

            if (request.TenantId == Guid.Empty)
            {
                return TenantAccountResult.Falla("No se pudo determinar el negocio de la cuenta.");
            }

            if (await CorreoEnUsoAsync(email))
            {
                return TenantAccountResult.Falla(
                    "Ese correo ya está registrado en LuxuryCloud. Usa un correo diferente.");
            }

            string password;
            if (request.Modo == TenantAccountCredentialMode.ContrasenaTemporal)
            {
                if (string.IsNullOrWhiteSpace(request.ContrasenaTemporal))
                {
                    return TenantAccountResult.Falla("Ingresa una contraseña temporal.");
                }

                password = request.ContrasenaTemporal.Trim();
            }
            else
            {
                password = GenerarPasswordSegura();
            }

            var usuario = new AppUsuario
            {
                UserName = email,
                Email = email,
                Name = request.DisplayName,
                PhoneNumber = request.Telefono,
                State = true,
                TenantId = request.TenantId
            };

            request.Configure?.Invoke(usuario);

            // Defensa en profundidad: Configure nunca puede mover la cuenta a otro negocio.
            usuario.TenantId = request.TenantId;

            TenantAccountResult? resultado = null;

            var executionStrategy = _context.Database.CreateExecutionStrategy();
            await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    var creado = await _userManager.CreateAsync(usuario, password);
                    if (!creado.Succeeded)
                    {
                        resultado = TenantAccountResult.Falla(creado.Errors.Select(TraducirError).ToArray());
                        await transaction.RollbackAsync(cancellationToken);
                        return;
                    }

                    var rol = await _userManager.AddToRoleAsync(usuario, request.Role);
                    if (!rol.Succeeded)
                    {
                        resultado = TenantAccountResult.Falla(rol.Errors.Select(error => error.Description).ToArray());
                        await transaction.RollbackAsync(cancellationToken);
                        return;
                    }

                    // El llamador enlaza su entidad DENTRO de la transacción: si falla, no queda
                    // una cuenta Identity suelta.
                    if (!await onCreatedInTransaction(usuario, cancellationToken))
                    {
                        resultado = TenantAccountResult.Falla(
                            "No fue posible completar el acceso. Intenta de nuevo.");
                        await transaction.RollbackAsync(cancellationToken);
                        return;
                    }

                    await transaction.CommitAsync(cancellationToken);

                    string? tokenCodificado = null;
                    if (request.Modo == TenantAccountCredentialMode.Invitacion)
                    {
                        tokenCodificado = await GenerarTokenContrasenaAsync(usuario);
                    }

                    _logger.LogInformation(
                        "Cuenta de acceso creada. TenantId {TenantId}. UserId {UserId}. Rol {Rol}. Modo {Modo}.",
                        request.TenantId,
                        usuario.Id,
                        request.Role,
                        request.Modo);

                    resultado = TenantAccountResult.Ok(usuario.Id, email, tokenCodificado);
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            });

            return resultado
                ?? TenantAccountResult.Falla("No fue posible habilitar el acceso. Intenta de nuevo.");
        }

        public async Task<TenantAccountResult> BloquearAsync(
            AppUsuario usuario,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(usuario);

            usuario.State = false;
            var update = await _userManager.UpdateAsync(usuario);
            if (!update.Succeeded)
            {
                return TenantAccountResult.Falla(update.Errors.Select(error => error.Description).ToArray());
            }

            // Invalida cualquier sesión activa de inmediato (SecurityStampValidator con
            // ValidationInterval = Zero): bloquear surte efecto en el siguiente request.
            await _userManager.UpdateSecurityStampAsync(usuario);

            return TenantAccountResult.Ok(usuario.Id, usuario.Email ?? string.Empty);
        }

        public async Task<TenantAccountResult> ReactivarAsync(
            AppUsuario usuario,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(usuario);

            usuario.State = true;
            await _userManager.SetLockoutEndDateAsync(usuario, null);
            var update = await _userManager.UpdateAsync(usuario);
            if (!update.Succeeded)
            {
                return TenantAccountResult.Falla(update.Errors.Select(error => error.Description).ToArray());
            }

            await _userManager.UpdateSecurityStampAsync(usuario);

            return TenantAccountResult.Ok(usuario.Id, usuario.Email ?? string.Empty);
        }

        public async Task<string> GenerarTokenContrasenaAsync(AppUsuario usuario)
        {
            var token = await _userManager.GeneratePasswordResetTokenAsync(usuario);
            return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        }

        public async Task<TenantAccountResult> CambiarCorreoAsync(
            AppUsuario usuario,
            string nuevoEmail,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(usuario);

            nuevoEmail = (nuevoEmail ?? string.Empty).Trim();

            if (!EsEmailValido(nuevoEmail))
            {
                return TenantAccountResult.Falla("Ingresa un correo electrónico válido.");
            }

            if (string.Equals(usuario.Email, nuevoEmail, StringComparison.OrdinalIgnoreCase))
            {
                return TenantAccountResult.Ok(usuario.Id, usuario.Email!);
            }

            if (await CorreoEnUsoAsync(nuevoEmail, usuario.Id))
            {
                return TenantAccountResult.Falla("Ese correo ya está registrado en LuxuryCloud.");
            }

            usuario.Email = nuevoEmail;
            usuario.UserName = nuevoEmail;

            var update = await _userManager.UpdateAsync(usuario);
            if (!update.Succeeded)
            {
                return TenantAccountResult.Falla(update.Errors.Select(TraducirError).ToArray());
            }

            await _userManager.UpdateSecurityStampAsync(usuario);

            return TenantAccountResult.Ok(usuario.Id, nuevoEmail);
        }

        private static string GenerarPasswordSegura()
        {
            // Cumple la política (8+ chars, mayúscula). Aleatoria; nunca se muestra ni se guarda.
            const string mayus = "ABCDEFGHJKLMNPQRSTUVWXYZ";
            const string minus = "abcdefghijkmnpqrstuvwxyz";
            const string nums = "23456789";
            const string simbolos = "!@#$%*";
            const string todos = mayus + minus + nums + simbolos;

            var sb = new StringBuilder();
            sb.Append(mayus[RandomNumberGenerator.GetInt32(mayus.Length)]);
            sb.Append(minus[RandomNumberGenerator.GetInt32(minus.Length)]);
            sb.Append(nums[RandomNumberGenerator.GetInt32(nums.Length)]);
            sb.Append(simbolos[RandomNumberGenerator.GetInt32(simbolos.Length)]);

            for (var i = 0; i < 12; i++)
            {
                sb.Append(todos[RandomNumberGenerator.GetInt32(todos.Length)]);
            }

            return sb.ToString();
        }

        private static string TraducirError(IdentityError error) => error.Code switch
        {
            "DuplicateUserName" or "DuplicateEmail" =>
                "Ese correo ya está registrado en LuxuryCloud.",
            "PasswordTooShort" =>
                "La contraseña es demasiado corta.",
            "PasswordRequiresUpper" =>
                "La contraseña debe incluir al menos una letra mayúscula.",
            _ => error.Description
        };
    }
}
