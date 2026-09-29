using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.Security;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Inversionistas
{
    /// <summary>
    /// Orquesta el envío del estado de participación: construye el documento desde el snapshot,
    /// genera el PDF, escribe la bitácora (<c>InvestorStatementEmailLog</c>), aplica idempotencia
    /// y reintentos acotados, y audita el resultado.
    /// </summary>
    public sealed class InvestorStatementEmailService : IInvestorStatementEmailService
    {
        /// <summary>Intentos por llamada. La clave de idempotencia de Resend impide duplicar el correo.</summary>
        private const int MaxIntentos = 2;

        private static readonly TimeSpan EsperaEntreIntentos = TimeSpan.FromMilliseconds(400);

        private readonly ApplicationDbContext _context;
        private readonly IInvestorStatementDocumentService _documentService;
        private readonly IInvestorStatementPdfService _pdfService;
        private readonly IInvestorStatementEmailRenderer _renderer;
        private readonly IInvestorStatementEmailSender _sender;
        private readonly IInvestorStatementService _statementService;
        private readonly IPlatformAuditService _auditService;
        private readonly ILogger<InvestorStatementEmailService> _logger;

        public InvestorStatementEmailService(
            ApplicationDbContext context,
            IInvestorStatementDocumentService documentService,
            IInvestorStatementPdfService pdfService,
            IInvestorStatementEmailRenderer renderer,
            IInvestorStatementEmailSender sender,
            IInvestorStatementService statementService,
            IPlatformAuditService auditService,
            ILogger<InvestorStatementEmailService> logger)
        {
            _context = context;
            _documentService = documentService;
            _pdfService = pdfService;
            _renderer = renderer;
            _sender = sender;
            _statementService = statementService;
            _auditService = auditService;
            _logger = logger;
        }

        public Task<InvestorStatementSendResult> SendAsync(
            int statementId,
            string? userId,
            CancellationToken cancellationToken = default) =>
            SendInternalAsync(statementId, recipientOverride: null, isTest: false, forceResend: false, userId, cancellationToken);

        public Task<InvestorStatementSendResult> ResendAsync(
            int statementId,
            string? userId,
            CancellationToken cancellationToken = default) =>
            SendInternalAsync(statementId, recipientOverride: null, isTest: false, forceResend: true, userId, cancellationToken);

        public Task<InvestorStatementSendResult> SendTestAsync(
            int statementId,
            string recipientEmail,
            string? userId,
            CancellationToken cancellationToken = default) =>
            SendInternalAsync(statementId, recipientEmail, isTest: true, forceResend: false, userId, cancellationToken);

        public async Task<(byte[] Content, string FileName)?> BuildPdfAsync(
            int statementId,
            CancellationToken cancellationToken = default)
        {
            var document = await _documentService.BuildAsync(statementId, cancellationToken);
            if (document is null)
            {
                return null;
            }

            return (_pdfService.Generar(document), document.NombreArchivo);
        }

        private async Task<InvestorStatementSendResult> SendInternalAsync(
            int statementId,
            string? recipientOverride,
            bool isTest,
            bool forceResend,
            string? userId,
            CancellationToken cancellationToken)
        {
            var statement = await _context.InvestorStatements
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == statementId, cancellationToken);

            if (statement is null)
            {
                return InvestorStatementSendResult.Failed("El estado de cuenta no existe o no pertenece a este negocio.");
            }

            // Regla dura: el correo sale del snapshot congelado, nunca de un borrador que puede cambiar.
            if (statement.Estado == InvestorStatementStatus.Draft)
            {
                return InvestorStatementSendResult.Failed(
                    "Finalizá el estado de cuenta antes de enviarlo: un borrador todavía puede cambiar de monto.");
            }

            if (statement.Estado == InvestorStatementStatus.Voided)
            {
                return InvestorStatementSendResult.Failed("El estado de cuenta está anulado y no puede enviarse.");
            }

            var document = await _documentService.BuildAsync(statementId, cancellationToken);
            if (document is null)
            {
                return InvestorStatementSendResult.Failed("No fue posible construir el estado de cuenta.");
            }

            var destino = NormalizeEmail(recipientOverride ?? document.InversionistaEmail);
            if (destino is null)
            {
                return InvestorStatementSendResult.Failed("El correo de destino no es válido.");
            }

            var secuencia = 0;

            if (!isTest)
            {
                var enviosPrevios = await _context.InvestorStatementEmailLogs
                    .AsNoTracking()
                    .Where(log => log.StatementId == statementId &&
                                  log.RecipientEmail == destino &&
                                  !log.IsTest &&
                                  log.Status == InvestorStatementEmailStatus.Sent)
                    .Select(log => log.ResendSequence)
                    .ToListAsync(cancellationToken);

                if (enviosPrevios.Count > 0)
                {
                    if (!forceResend)
                    {
                        // Idempotencia: el envío automático o un doble clic no reenvía.
                        await RegisterSkippedAsync(statement, document, destino, userId, cancellationToken);
                        return InvestorStatementSendResult.Skipped(
                            "Este estado de cuenta ya fue enviado a ese correo. Usá \"Reenviar\" si necesitás mandarlo de nuevo.");
                    }

                    secuencia = enviosPrevios.Max() + 1;
                }
            }

            var subject = Truncate(document.Asunto, 300)!;
            if (isTest)
            {
                subject = Truncate("[Prueba] " + subject, 300)!;
            }

            var log = new InvestorStatementEmailLog
            {
                StatementId = statementId,
                RecipientEmail = destino,
                Subject = subject,
                Status = InvestorStatementEmailStatus.Pending,
                IsTest = isTest,
                ResendSequence = secuencia,
                TriggeredByUserId = userId,
                ContentHash = ComputeContentHash(document),
                CreatedAtUtc = DateTime.UtcNow
            };

            _context.InvestorStatementEmailLogs.Add(log);
            await _context.SaveChangesAsync(cancellationToken);

            byte[]? pdf = null;
            try
            {
                pdf = _pdfService.Generar(document);
            }
            catch (Exception ex)
            {
                // Un fallo del PDF no debe bloquear el correo: se envía sin adjunto y queda el aviso.
                _logger.LogError(ex, "No se pudo generar el PDF del estado {StatementId}. Se enviará sin adjunto.", statementId);
            }

            var html = _renderer.RenderHtml(document);
            var texto = _renderer.RenderText(document);

            // Clave estable por estado/destinatario/secuencia: un reintento nunca duplica el correo.
            var idempotencyKey = BuildIdempotencyKey(document, destino, isTest, secuencia);

            InvestorStatementEmailSendAttempt attempt = new(false, null, "No se intentó el envío.");

            for (var intento = 1; intento <= MaxIntentos; intento++)
            {
                attempt = await _sender.SendAsync(
                    document,
                    destino,
                    subject,
                    html,
                    texto,
                    pdf,
                    idempotencyKey,
                    cancellationToken);

                if (attempt.Success)
                {
                    break;
                }

                if (intento < MaxIntentos)
                {
                    _logger.LogWarning(
                        "Reintento {Intento}/{Maximo} del estado {StatementId} hacia {Email}.",
                        intento,
                        MaxIntentos,
                        statementId,
                        SensitiveDataMasker.MaskEmail(destino));

                    await Task.Delay(EsperaEntreIntentos, cancellationToken);
                }
            }

            if (attempt.Success)
            {
                log.Status = InvestorStatementEmailStatus.Sent;
                log.ProviderMessageId = Truncate(attempt.ProviderMessageId, 200);
                log.SentAtUtc = DateTime.UtcNow;
            }
            else
            {
                log.Status = InvestorStatementEmailStatus.Failed;
                // Mensaje saneado: solo el tipo de error del proveedor, sin datos del inversionista.
                log.ErrorMessage = Truncate(attempt.Error, 500);
            }

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (log.Status == InvestorStatementEmailStatus.Sent && !isTest)
            {
                // Carrera contra el índice único de envío real: otro proceso ya lo marcó como enviado.
                // El correo del proveedor es idempotente por clave, así que no hay duplicado real.
                _logger.LogWarning(
                    ex,
                    "Envío real duplicado detectado por índice único (estado {StatementId}, {Email}).",
                    statementId,
                    SensitiveDataMasker.MaskEmail(destino));

                _context.Entry(log).State = EntityState.Detached;

                return InvestorStatementSendResult.Skipped("Este estado de cuenta ya había sido enviado a ese correo.");
            }

            if (!attempt.Success)
            {
                return InvestorStatementSendResult.Failed(
                    $"No se pudo enviar el estado de cuenta: {attempt.Error ?? "error desconocido"}.");
            }

            if (!isTest)
            {
                await _statementService.MarkAsSentAsync(statementId, cancellationToken);

                await _auditService.TryLogAsync(
                    new PlatformAuditEntry
                    {
                        Action = forceResend
                            ? PlatformAuditActions.InvestorStatementResent
                            : PlatformAuditActions.InvestorStatementSent,
                        EntityType = PlatformAuditEntityTypes.InvestorStatement,
                        EntityId = statementId.ToString(),
                        TenantId = statement.TenantId,
                        AfterJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            Destinatario = SensitiveDataMasker.MaskEmail(destino),
                            Secuencia = secuencia,
                            document.ParticipacionCalculada,
                            document.SaldoPendiente
                        })
                    },
                    cancellationToken);
            }

            return InvestorStatementSendResult.Sent(
                isTest
                    ? $"Correo de prueba enviado a {destino}."
                    : $"Estado de cuenta enviado a {destino}.");
        }

        private async Task RegisterSkippedAsync(
            InvestorStatement statement,
            InvestorStatementDocument document,
            string destino,
            string? userId,
            CancellationToken cancellationToken)
        {
            _context.InvestorStatementEmailLogs.Add(new InvestorStatementEmailLog
            {
                StatementId = statement.Id,
                RecipientEmail = destino,
                Subject = Truncate(document.Asunto, 300)!,
                Status = InvestorStatementEmailStatus.Skipped,
                IsTest = false,
                ResendSequence = 0,
                TriggeredByUserId = userId,
                ErrorMessage = "Ya existe un envío real exitoso para este estado y destinatario.",
                ContentHash = ComputeContentHash(document),
                CreatedAtUtc = DateTime.UtcNow
            });

            await _context.SaveChangesAsync(cancellationToken);
        }

        private static string BuildIdempotencyKey(
            InvestorStatementDocument document,
            string destino,
            bool isTest,
            int secuencia)
        {
            if (isTest)
            {
                // Las pruebas SÍ pueden repetirse: clave nueva en cada intento de prueba.
                return $"invstmt-test-{Guid.NewGuid():N}";
            }

            var emailHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(destino.ToLowerInvariant())))
                .ToLowerInvariant()[..16];

            return $"invstmt-{document.TenantId:N}-{document.StatementId}-{secuencia}-{emailHash}";
        }

        private static string ComputeContentHash(InvestorStatementDocument document)
        {
            var payload = string.Join(
                '|',
                document.StatementId.ToString(CultureInfo.InvariantCulture),
                document.PeriodoInicio.ToString("yyyy-MM-dd"),
                document.PeriodoFin.ToString("yyyy-MM-dd"),
                document.GananciaDistribuible.ToString(CultureInfo.InvariantCulture),
                document.ParticipacionPorcentaje.ToString(CultureInfo.InvariantCulture),
                document.ParticipacionCalculada.ToString(CultureInfo.InvariantCulture),
                document.TotalPagado.ToString(CultureInfo.InvariantCulture),
                document.SaldoPendiente.ToString(CultureInfo.InvariantCulture));

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        }

        private static string? NormalizeEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return null;
            }

            var trimmed = email.Trim();
            if (!MailAddress.TryCreate(trimmed, out var parsed) ||
                !string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return trimmed.ToLowerInvariant();
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }
    }
}
