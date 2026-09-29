using System.Globalization;
using System.Text;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Common;
using LuxuryApp.Services.WhatsApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Clientes
{
    /// <inheritdoc />
    public sealed class ClienteIdentityService : IClienteIdentityService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly IOptionsMonitor<MetaWhatsAppOptions> _whatsAppOptions;

        /// <param name="whatsAppOptions">
        /// Solo se lee <c>DefaultCountryCode</c>: es el codigo de pais canonico del sistema, el
        /// mismo con el que se normalizan los numeros para enviar mensajes. Se reutiliza a
        /// proposito para que "el telefono del cliente" signifique lo mismo en todo el producto.
        /// </param>
        public ClienteIdentityService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider,
            IOptionsMonitor<MetaWhatsAppOptions> whatsAppOptions)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
            _whatsAppOptions = whatsAppOptions;
        }

        public async Task<ClienteIdentityResolution> ResolveAsync(
            string? nombre,
            string? telefono,
            CancellationToken cancellationToken = default)
        {
            var keys = PhoneNumberNormalizer.BuildMatchKeys(telefono, CountryCode);
            if (keys is null)
            {
                // Sin telefono no hay identidad: el nombre solo NUNCA fusiona personas.
                return ClienteIdentityResolution.InsufficientData;
            }

            var comparisonKeys = keys.ComparisonKeys;
            var literal = telefono!.Trim();

            // La comparacion normalizada se hace en SQL con la MISMA cadena de REPLACE que el
            // modulo de Clientes ya usaba en produccion. No se agrega columna ni indice: la tabla
            // esta acotada por tenant (RLS + global query filter) y el volumen por negocio es bajo.
            var candidatos = await _context.Clientes
                .AsNoTracking()
                .Where(c =>
                    c.NumeroTelefono == literal ||
                    comparisonKeys.Contains(
                        c.NumeroTelefono
                            .Replace(" ", string.Empty)
                            .Replace("-", string.Empty)
                            .Replace("(", string.Empty)
                            .Replace(")", string.Empty)
                            .Replace("+", string.Empty)
                            .Replace(".", string.Empty)
                            .Replace("/", string.Empty)))
                .OrderBy(c => c.Id)
                .Select(c => new ClienteIdentityMatch(
                    c.Id,
                    c.Nombre,
                    c.NumeroTelefono,
                    c.CorreoElectronico,
                    c.AceptaMensajesWhatsApp))
                .ToListAsync(cancellationToken);

            if (candidatos.Count == 0)
            {
                return ClienteIdentityResolution.NotFound;
            }

            if (candidatos.Count > 1)
            {
                // Duplicados historicos: se representa la ambiguedad, no se elige por First().
                return new ClienteIdentityResolution(ClienteIdentityStatus.AmbiguousPhoneMatch, candidatos);
            }

            var status = NamesMatch(nombre, candidatos[0].Nombre)
                ? ClienteIdentityStatus.ExistingExactMatch
                : ClienteIdentityStatus.ExistingPhoneMatchWithDifferentName;

            return new ClienteIdentityResolution(status, candidatos);
        }

        public async Task<ClienteIdentityMatch?> FindByIdAsync(
            int clienteId,
            CancellationToken cancellationToken = default)
        {
            if (clienteId <= 0)
            {
                return null;
            }

            return await _context.Clientes
                .AsNoTracking()
                .Where(c => c.Id == clienteId)
                .Select(c => new ClienteIdentityMatch(
                    c.Id,
                    c.Nombre,
                    c.NumeroTelefono,
                    c.CorreoElectronico,
                    c.AceptaMensajesWhatsApp))
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<ClienteIdentityMatch> RegisterAsync(
            ClienteRegistrationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var nombre = (request.Nombre ?? string.Empty).Trim();
            var telefono = (request.Telefono ?? string.Empty).Trim();

            if (nombre.Length == 0 || telefono.Length == 0)
            {
                throw new InvalidOperationException("No es posible registrar un cliente sin nombre y telefono.");
            }

            var cliente = new ClientesModel
            {
                Nombre = nombre,
                NumeroTelefono = telefono,
                // Mismo default que el alta manual (ClienteDefaults): el cliente creado desde una
                // cita queda igual que uno creado desde el modulo de Clientes. Es solo la
                // frecuencia INICIAL; en cuanto tenga dos dias de visita manda el promedio real.
                FrecuenciaVisita = ClienteDefaults.InitialVisitFrequencyDays,
                FechaUltimaVisita = _businessDateTimeProvider.Today(),
                AceptaMensajesWhatsApp = request.AceptaMensajesWhatsApp
            };

            if (request.AceptaMensajesWhatsApp)
            {
                cliente.WhatsAppConsentUpdatedAtUtc = request.ConsentCapturedAtUtc ?? DateTime.UtcNow;
                cliente.WhatsAppConsentSource = request.ConsentSource ?? WhatsAppConsentSources.CitaManual;
                cliente.WhatsAppConsentTextVersion = WhatsAppConsentTextVersions.WaOptInV1;
                cliente.WhatsAppConsentCapturedByUserId = request.ConsentCapturedByUserId;
            }

            // El TenantId lo asigna el guard del DbContext: nunca viaja desde el navegador.
            _context.Clientes.Add(cliente);
            await _context.SaveChangesAsync(cancellationToken);

            return new ClienteIdentityMatch(
                cliente.Id,
                cliente.Nombre,
                cliente.NumeroTelefono,
                cliente.CorreoElectronico,
                cliente.AceptaMensajesWhatsApp);
        }

        private string CountryCode =>
            PhoneNumberNormalizer.ResolveCountryCode(_whatsAppOptions.CurrentValue?.DefaultCountryCode);

        /// <summary>
        /// Igualdad "razonable" de nombres para distinguir coincidencia exacta de coincidencia solo
        /// por telefono: ignora mayusculas, tildes y espacios repetidos. NO se usa nunca para
        /// identificar: solo para decidir que mensaje mostrar.
        /// </summary>
        internal static bool NamesMatch(string? escrito, string? persistido)
        {
            var normalizadoPersistido = NormalizeName(persistido);

            return normalizadoPersistido.Length > 0 &&
                string.Equals(NormalizeName(escrito), normalizadoPersistido, StringComparison.Ordinal);
        }

        private static string NormalizeName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var collapsed = string.Join(
                " ",
                value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            var decomposed = collapsed.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);

            foreach (var character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(character);
                }
            }

            return builder
                .ToString()
                .Normalize(NormalizationForm.FormC)
                .ToUpperInvariant();
        }
    }
}
