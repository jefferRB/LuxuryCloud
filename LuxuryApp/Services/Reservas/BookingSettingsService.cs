using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Services.Tenant;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Reservas
{
    public sealed partial class BookingSettingsService : IBookingSettingsService
    {
        // Palabras reservadas que no pueden usarse como slug (chocarían con rutas del sistema).
        private static readonly HashSet<string> ReservedSlugs = new(StringComparer.OrdinalIgnoreCase)
        {
            "admin", "login", "api", "app", "reservar", "dashboard", "plataforma",
            "account", "accounts", "identity", "static", "assets", "www",
            "home", "billing", "platform", "comprobantes", "calendar", "miportal",
            "soporte", "privacidad", "contrato", "calendario", "reservas",
            "clientes", "funcionarios", "productos", "ingresos", "egresos", "informacion"
        };

        private const int SlugMinLength = 3;
        private const int SlugMaxLength = 60;

        private readonly ApplicationDbContext _context;
        private readonly ITenantProvider _tenantProvider;
        private readonly ITenantDisplayNameService _tenantDisplayNameService;

        public BookingSettingsService(
            ApplicationDbContext context,
            ITenantProvider tenantProvider,
            ITenantDisplayNameService tenantDisplayNameService)
        {
            _context = context;
            _tenantProvider = tenantProvider;
            _tenantDisplayNameService = tenantDisplayNameService;
        }

        public async Task<BookingSettingsViewModel> BuildSettingsViewModelAsync(CancellationToken cancellationToken = default)
        {
            var settings = await _context.TenantBookingSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);

            var horarios = await _context.TenantBookingBusinessHours
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            var nombreNegocio = await GetTenantNameAsync(cancellationToken);

            // La jornada se lee SIEMPRE por el mismo camino que el motor de disponibilidad: con
            // filas manda la tabla nueva; sin filas, se reconstruye de las columnas antiguas. Así
            // la pantalla muestra exactamente lo que el enlace público va a ofrecer.
            var schedule = BookingWeeklySchedule.From(settings, horarios);

            if (settings is null)
            {
                // Sugerencia de slug único derivada del nombre del negocio (sin chocar con otros tenants).
                var sugerido = await GenerateUniqueSlugAsync(nombreNegocio, GetCurrentTenantId(), cancellationToken);

                return new BookingSettingsViewModel
                {
                    NombreNegocio = nombreNegocio,
                    PublicBookingSlug = sugerido,
                    Horario = BuildHorarioViewModel(
                        BookingWeeklySchedule.From(
                            new TenantBookingSettings { WorkingDaysMask = TenantBookingSettings.DefaultWorkingDaysMask },
                            horarios: null))
                };
            }

            return new BookingSettingsViewModel
            {
                PublicBookingEnabled = settings.PublicBookingEnabled,
                PublicBookingSlug = settings.PublicBookingSlug,
                PublicBookingAllowEmployeeSelection = settings.PublicBookingAllowEmployeeSelection,
                PublicBookingAllowAnyEmployee = settings.PublicBookingAllowAnyEmployee,
                PublicBookingShowEmployeePhotos = settings.PublicBookingShowEmployeePhotos,
                PublicBookingMinAdvanceMinutes = settings.PublicBookingMinAdvanceMinutes,
                PublicBookingMaxDaysAhead = settings.PublicBookingMaxDaysAhead,
                PublicBookingWelcomeMessage = settings.PublicBookingWelcomeMessage,
                PublicBookingConfirmationMessage = settings.PublicBookingConfirmationMessage,
                SlotIntervalMinutes = settings.SlotIntervalMinutes,
                Horario = BuildHorarioViewModel(schedule),
                NombreNegocio = nombreNegocio
            };
        }

        /// <summary>
        /// Proyecta la jornada resuelta a las siete filas del formulario. Un día cerrado conserva
        /// horas visibles (las últimas guardadas o las por defecto) para que al reabrirlo el
        /// usuario no tenga que reescribirlas.
        /// </summary>
        private static List<BookingDayScheduleViewModel> BuildHorarioViewModel(BookingWeeklySchedule schedule)
        {
            var filas = new List<BookingDayScheduleViewModel>(7);

            foreach (var indice in BookingDayNames.OrdenSemana)
            {
                var ventana = schedule.GetDay((DayOfWeek)indice);

                filas.Add(new BookingDayScheduleViewModel
                {
                    DiaSemana = indice,
                    Abierto = ventana.Abierto,
                    Apertura = ventana.Abierto ? ventana.Apertura : TenantBookingSettings.DefaultOpenTime,
                    Cierre = ventana.Abierto ? ventana.Cierre : TenantBookingSettings.DefaultCloseTime
                });
            }

            return filas;
        }

        public async Task SaveSettingsAsync(
            BookingSettingsViewModel input,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            if (input.SlotIntervalMinutes < 5 || input.SlotIntervalMinutes > 240)
            {
                throw new BookingValidationException("El intervalo entre citas debe estar entre 5 y 240 minutos.");
            }

            // Jornada semanal validada en el SERVIDOR: rango de día, cierre posterior a apertura y
            // un único registro por día. Nada de esto se confía al formulario.
            var jornada = NormalizeHorario(input.Horario);
            var mask = BuildWorkingDaysMask(jornada);

            string? slug = null;
            if (input.PublicBookingEnabled || !string.IsNullOrWhiteSpace(input.PublicBookingSlug))
            {
                slug = await ResolveValidSlugAsync(input.PublicBookingSlug, cancellationToken);

                if (input.PublicBookingEnabled && string.IsNullOrWhiteSpace(slug))
                {
                    throw new BookingValidationException("Necesitas un enlace válido para activar las reservas online.", nameof(BookingSettingsViewModel.PublicBookingSlug));
                }
            }

            if (input.PublicBookingEnabled && mask == 0)
            {
                throw new BookingValidationException("Abrí al menos un día de la semana para activar las reservas.");
            }

            var settings = await _context.TenantBookingSettings
                .FirstOrDefaultAsync(cancellationToken);

            if (settings is null)
            {
                settings = new TenantBookingSettings();
                _context.TenantBookingSettings.Add(settings);
            }

            settings.PublicBookingEnabled = input.PublicBookingEnabled;
            settings.PublicBookingSlug = slug;
            settings.PublicBookingMode = PublicBookingModes.ManualApproval;
            settings.PublicBookingAllowEmployeeSelection = input.PublicBookingAllowEmployeeSelection;
            settings.PublicBookingAllowAnyEmployee = input.PublicBookingAllowAnyEmployee;
            settings.PublicBookingShowEmployeePhotos = input.PublicBookingShowEmployeePhotos;
            settings.PublicBookingMinAdvanceMinutes = Math.Clamp(input.PublicBookingMinAdvanceMinutes, 0, 43200);
            settings.PublicBookingMaxDaysAhead = Math.Clamp(input.PublicBookingMaxDaysAhead, 1, 365);
            settings.PublicBookingWelcomeMessage = Trim(input.PublicBookingWelcomeMessage, 500);
            settings.PublicBookingConfirmationMessage = Trim(input.PublicBookingConfirmationMessage, 500);
            settings.SlotIntervalMinutes = input.SlotIntervalMinutes;

            // Columnas antiguas: se mantienen sincronizadas como envolvente de la semana (día
            // abierto más temprano / cierre más tardío). No las lee el motor mientras existan filas
            // por día, pero conservarlas coherentes evita que un consumidor heredado vea datos
            // congelados y hace reversible el despliegue.
            settings.WorkingDaysMask = mask;
            if (jornada.Count > 0)
            {
                settings.OpenTime = jornada.Min(dia => dia.Apertura);
                settings.CloseTime = jornada.Max(dia => dia.Cierre);
            }

            settings.UpdatedAtUtc = DateTime.UtcNow;
            settings.UpdatedByUserId = userId;

            await UpsertHorarioAsync(jornada, cancellationToken);

            // UN solo SaveChanges: EF lo ejecuta dentro de una única transacción, así que la
            // configuración y las siete jornadas quedan consistentes o no se guarda nada. No hace
            // falta abrir una transacción manual para esto.
            await _context.SaveChangesAsync(cancellationToken);
        }

        /// <summary>
        /// Valida y normaliza la jornada recibida del formulario: descarta días fuera de 0..6,
        /// se queda con la PRIMERA aparición de cada día (un POST manipulado no puede duplicar) y
        /// exige cierre posterior a la apertura en los días abiertos.
        /// </summary>
        private static List<BookingDayScheduleViewModel> NormalizeHorario(IEnumerable<BookingDayScheduleViewModel>? horario)
        {
            var resultado = new List<BookingDayScheduleViewModel>(7);

            if (horario is null)
            {
                return resultado;
            }

            var vistos = new HashSet<int>();

            foreach (var dia in horario)
            {
                if (dia is null || dia.DiaSemana is < 0 or > 6 || !vistos.Add(dia.DiaSemana))
                {
                    continue;
                }

                if (!dia.Abierto)
                {
                    continue;
                }

                if (dia.Cierre <= dia.Apertura)
                {
                    throw new BookingValidationException(
                        $"En {BookingDayNames.Largo(dia.DiaSemana).ToLowerInvariant()} la hora de cierre debe ser posterior a la de apertura.");
                }

                resultado.Add(dia);
            }

            return resultado;
        }

        private static int BuildWorkingDaysMask(IEnumerable<BookingDayScheduleViewModel> jornada)
        {
            var mask = 0;
            foreach (var dia in jornada)
            {
                mask |= 1 << dia.DiaSemana;
            }

            return mask;
        }

        /// <summary>
        /// Upsert idempotente de las siete jornadas. Se actualiza la fila existente de cada día y
        /// solo se inserta la que falta, de modo que guardar dos veces deja exactamente el mismo
        /// estado. El índice único (TenantId, DiaSemana) es la última garantía en la base.
        /// </summary>
        private async Task UpsertHorarioAsync(
            IReadOnlyCollection<BookingDayScheduleViewModel> jornada,
            CancellationToken cancellationToken)
        {
            var existentes = await _context.TenantBookingBusinessHours
                .ToDictionaryAsync(hora => hora.DiaSemana, cancellationToken);

            var abiertosPorDia = jornada.ToDictionary(dia => (DayOfWeek)dia.DiaSemana);
            var ahora = DateTime.UtcNow;

            for (var indice = 0; indice < 7; indice++)
            {
                var dia = (DayOfWeek)indice;
                abiertosPorDia.TryGetValue(dia, out var deseado);

                var abierto = deseado is not null;
                // Un día cerrado conserva un rango válido (el guardado o el de fábrica): la
                // restricción CHECK de la base exige OpenTime < CloseTime también ahí.
                var apertura = deseado?.Apertura
                    ?? (existentes.TryGetValue(dia, out var previa) ? previa.OpenTime : TenantBookingSettings.DefaultOpenTime);
                var cierre = deseado?.Cierre
                    ?? (existentes.TryGetValue(dia, out var previaCierre) ? previaCierre.CloseTime : TenantBookingSettings.DefaultCloseTime);

                if (cierre <= apertura)
                {
                    apertura = TenantBookingSettings.DefaultOpenTime;
                    cierre = TenantBookingSettings.DefaultCloseTime;
                }

                if (existentes.TryGetValue(dia, out var fila))
                {
                    if (fila.IsEnabled == abierto && fila.OpenTime == apertura && fila.CloseTime == cierre)
                    {
                        continue; // sin cambios reales: no se toca la fila ni su UpdatedAtUtc
                    }

                    fila.IsEnabled = abierto;
                    fila.OpenTime = apertura;
                    fila.CloseTime = cierre;
                    fila.UpdatedAtUtc = ahora;
                    continue;
                }

                _context.TenantBookingBusinessHours.Add(new TenantBookingBusinessHour
                {
                    DiaSemana = dia,
                    IsEnabled = abierto,
                    OpenTime = apertura,
                    CloseTime = cierre,
                    CreatedAtUtc = ahora,
                    UpdatedAtUtc = ahora
                });
            }
        }

        public async Task<PublicBookingTenantContext?> ResolvePublicBySlugAsync(
            string slug,
            CancellationToken cancellationToken = default)
        {
            var normalized = NormalizeSlug(slug);
            if (string.IsNullOrEmpty(normalized))
            {
                return null;
            }

            // IgnoreQueryFilters: la resolución ocurre antes de tener contexto de tenant.
            var match = await _context.TenantBookingSettings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => s.PublicBookingSlug == normalized && s.PublicBookingEnabled)
                .Select(s => new
                {
                    s.TenantId,
                    s.PublicBookingSlug,
                    s.PublicBookingWelcomeMessage,
                    s.PublicBookingConfirmationMessage,
                    s.PublicBookingAllowEmployeeSelection,
                    s.PublicBookingAllowAnyEmployee,
                    s.PublicBookingShowEmployeePhotos,
                    s.PublicBookingMinAdvanceMinutes,
                    s.PublicBookingMaxDaysAhead,
                    TenantActivo = _context.Tenants
                        .Any(t => t.Id == s.TenantId && t.Activo)
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (match is null || !match.TenantActivo)
            {
                return null;
            }

            var nombreNegocio = await _tenantDisplayNameService.GetTenantDisplayNameAsync(
                match.TenantId,
                cancellationToken);

            return new PublicBookingTenantContext
            {
                TenantId = match.TenantId,
                NombreNegocio = nombreNegocio,
                Slug = match.PublicBookingSlug!,
                MensajeBienvenida = match.PublicBookingWelcomeMessage,
                MensajeConfirmacion = match.PublicBookingConfirmationMessage,
                PermiteElegirFuncionario = match.PublicBookingAllowEmployeeSelection,
                PermiteCualquierFuncionario = match.PublicBookingAllowAnyEmployee,
                MostrarFotosFuncionarios = match.PublicBookingShowEmployeePhotos,
                MinAdvanceMinutes = match.PublicBookingMinAdvanceMinutes,
                MaxDaysAhead = match.PublicBookingMaxDaysAhead
            };
        }

        public async Task<string?> GetCurrentSlugAsync(CancellationToken cancellationToken = default)
        {
            return await _context.TenantBookingSettings
                .AsNoTracking()
                .Where(s => s.PublicBookingEnabled)
                .Select(s => s.PublicBookingSlug)
                .FirstOrDefaultAsync(cancellationToken);
        }

        private async Task<string?> ResolveValidSlugAsync(string? rawSlug, CancellationToken cancellationToken)
        {
            var currentTenantId = GetCurrentTenantId();
            var slug = NormalizeSlug(rawSlug);

            // Si el usuario lo dejó vacío, autogeneramos una variante única desde el nombre del negocio.
            if (string.IsNullOrEmpty(slug))
            {
                return await GenerateUniqueSlugAsync(
                    await GetTenantNameAsync(cancellationToken),
                    currentTenantId,
                    cancellationToken);
            }

            if (slug.Length < SlugMinLength)
            {
                throw new BookingValidationException("El enlace debe tener al menos 3 caracteres.", nameof(BookingSettingsViewModel.PublicBookingSlug));
            }

            if (ReservedSlugs.Contains(slug))
            {
                throw new BookingValidationException("Ese enlace está reservado por el sistema. Probá con otro nombre.", nameof(BookingSettingsViewModel.PublicBookingSlug));
            }

            // Único entre tenants (excluyendo el propio tenant actual, que puede conservar su slug).
            if (await SlugInUseAsync(slug, currentTenantId, cancellationToken))
            {
                throw new BookingValidationException("Este enlace ya está en uso. Probá con otro nombre.", nameof(BookingSettingsViewModel.PublicBookingSlug));
            }

            return slug;
        }

        /// <summary>
        /// Genera un slug único a partir del nombre del negocio. Si la base ya existe (o es
        /// reservada), agrega un sufijo incremental: barberia-elite, barberia-elite-2, etc.
        /// </summary>
        private async Task<string> GenerateUniqueSlugAsync(
            string? baseName,
            Guid currentTenantId,
            CancellationToken cancellationToken)
        {
            var baseSlug = NormalizeSlug(baseName);

            if (baseSlug.Length < SlugMinLength)
            {
                baseSlug = "negocio";
            }

            // Deja espacio para el sufijo "-NN" sin exceder el máximo.
            if (baseSlug.Length > SlugMaxLength - 4)
            {
                baseSlug = baseSlug[..(SlugMaxLength - 4)].Trim('-');
            }

            var candidate = baseSlug;
            var sufijo = 2;

            while (ReservedSlugs.Contains(candidate) ||
                   await SlugInUseAsync(candidate, currentTenantId, cancellationToken))
            {
                candidate = $"{baseSlug}-{sufijo}";
                sufijo++;

                if (sufijo > 1000)
                {
                    // Salida de seguridad: sufijo aleatorio para no entrar en bucle.
                    candidate = $"{baseSlug}-{Guid.NewGuid():N}"[..Math.Min(SlugMaxLength, baseSlug.Length + 9)];
                    break;
                }
            }

            return candidate;
        }

        private Task<bool> SlugInUseAsync(string slug, Guid currentTenantId, CancellationToken cancellationToken) =>
            _context.TenantBookingSettings
                .IgnoreQueryFilters()
                .AnyAsync(s => s.PublicBookingSlug == slug && s.TenantId != currentTenantId, cancellationToken);

        private Guid GetCurrentTenantId() =>
            _tenantProvider.HasTenant() ? _tenantProvider.GetTenantId() : Guid.Empty;

        private async Task<string> GetTenantNameAsync(CancellationToken cancellationToken)
        {
            var tenantId = GetCurrentTenantId();
            if (tenantId == Guid.Empty)
            {
                return string.Empty;
            }

            return await _tenantDisplayNameService.GetTenantDisplayNameAsync(tenantId, cancellationToken);
        }

        // ─────────────── Helpers de slug y días ───────────────

        public static string NormalizeSlug(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var lower = RemoveDiacritics(value.Trim().ToLowerInvariant());
            var cleaned = SlugInvalidCharsRegex().Replace(lower, "-");
            cleaned = SlugMultiHyphenRegex().Replace(cleaned, "-").Trim('-');

            if (cleaned.Length > SlugMaxLength)
            {
                cleaned = cleaned[..SlugMaxLength].Trim('-');
            }

            return cleaned;
        }

        private static string RemoveDiacritics(string text)
        {
            var normalized = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        private static string? Trim(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
        }

        [GeneratedRegex("[^a-z0-9]+")]
        private static partial Regex SlugInvalidCharsRegex();

        [GeneratedRegex("-{2,}")]
        private static partial Regex SlugMultiHyphenRegex();
    }
}
