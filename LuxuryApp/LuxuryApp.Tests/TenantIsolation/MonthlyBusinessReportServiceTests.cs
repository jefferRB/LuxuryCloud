using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Productos;
using LuxuryApp.Models.Reports;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class MonthlyBusinessReportServiceTests
    {
        // ─────────────── Generación ───────────────

        [Fact]
        public async Task GenerateAsync_WithData_BuildsFinancialAndOperationalSummary()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Corte", 100m);
            var producto = await SeedProductoAsync(context, "Shampoo", 200m);

            await SeedCobroAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 5, 10, 0, 0), 100m, "EFECTIVO", servicioId: servicio.Id);
            await SeedCobroAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 12, 11, 0, 0), 100m, "SINPE", servicioId: servicio.Id);
            await SeedCobroAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 20, 15, 0, 0), 200m, "TARJETA", productoId: producto.IdProducto);

            var categoria = await SeedCategoriaAsync(context, "Operativo");
            await SeedEgresoAsync(context, categoria.Id, new DateTime(2026, 4, 15, 9, 0, 0), 50m);

            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 5, 10, 0, 0), servicio.Id);
            await SeedCitaAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 12, 11, 0, 0), servicio.Id);

            var sender = new FakeMonthlyReportEmailSender();
            var service = ControllerTestSupport.CreateMonthlyBusinessReportService(context, tenantProvider, sender, "Barbería Luxury");

            var report = await service.GenerateAsync(tenantId, 2026, 4);

            Assert.Equal(tenantId, report.TenantId);
            Assert.Equal("Barbería Luxury", report.NombreNegocio);
            Assert.Equal(4, report.Mes);
            Assert.Equal(2026, report.Anio);
            Assert.Equal("Abril", report.MesNombre);
            Assert.True(report.TieneActividad);

            // Finanzas: mismos números del Dashboard Financiero, que ahora usa el motor único.
            // El IVA se calcula por línea de cobro y luego se suma, no dividiendo el total del mes.
            Assert.Equal(400m, report.Ingresos);
            Assert.Equal(353.99m, report.TotalSinImpuestos);
            Assert.Equal(46.01m, report.Impuestos);

            // El correo pasó a reportar la MISMA "Ganancia del mes" que el Dashboard (devengado).
            // Antes reportaba el resultado de CAJA (353,99 − 50 = 303,99), que no es la ganancia.
            //
            //   Liquidaciones devengadas = 50 % sobre base de servicios (88,50 + 88,50 = 177,00) → 88,50
            //                            + 10 % sobre base de producto  (176,99)                  → 17,70
            //                            = 106,20
            //   Costos y gastos del mes  = 50 (gasto operativo) + 106,20 = 156,20
            //   Ganancia del mes         = 353,99 − 156,20 = 197,79
            //   Margen                   = 197,79 / 400 × 100 = 49,4475 → 49,45 (half-even)
            Assert.Equal(156.20m, report.Egresos);
            Assert.Equal(197.79m, report.GananciaReal);
            Assert.Equal(49.45m, report.MargenGanancia);

            // La CAJA se informa aparte y sigue siendo el único egreso de abril.
            Assert.Equal(50m, report.SalidasCaja);
            Assert.Equal(200m, report.ServiciosGeneradosMonto);
            Assert.Equal(200m, report.ProductosGeneradosMonto);
            Assert.Equal(100m, report.IngresosEfectivo);
            Assert.Equal(100m, report.IngresosSinpe);
            Assert.Equal(200m, report.IngresosTarjeta);

            // Operación: mismos números de Información del negocio.
            Assert.Equal(2, report.ServiciosRealizados);
            Assert.Equal(1, report.ProductosVendidos);
            Assert.Equal("Corte", report.ServicioMasSolicitadoNombre);
            Assert.Equal(2, report.ServicioMasSolicitadoCantidad);
            Assert.Equal("Shampoo", report.ProductoMasVendidoNombre);
            Assert.Equal("Ana", report.FuncionarioEstrellaNombre);
            Assert.Equal(2, report.FuncionarioEstrellaCantidadCitas);

            // Mensajes interpretativos (margen >= 25% => muy saludable) y formato en colones.
            Assert.Contains("saludable", report.ComentarioMargen);
            Assert.Contains("₡", report.ResumenEjecutivoTexto);
            Assert.Contains("Ana", report.ComentarioFuncionarioEstrella);
        }

        [Fact]
        public async Task GenerateAsync_MonthWithoutActivity_BuildsEmptyReportWithoutErrors()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var sender = new FakeMonthlyReportEmailSender();
            var service = ControllerTestSupport.CreateMonthlyBusinessReportService(context, tenantProvider, sender);

            var report = await service.GenerateAsync(tenantId, 2026, 2);

            Assert.False(report.TieneActividad);
            Assert.Equal(0m, report.Ingresos);
            Assert.Equal(0m, report.MargenGanancia); // sin división entre cero
            Assert.Equal(string.Empty, report.ServicioMasSolicitadoNombre);
            Assert.Equal(string.Empty, report.FuncionarioEstrellaNombre);
            Assert.Contains("no se registró actividad", report.ResumenEjecutivoTexto);
            Assert.Contains("Sin ingresos registrados", report.ComentarioMargen);
        }

        [Fact]
        public async Task GenerateAsync_ExpensesAboveIncome_ReportsNegativeMarginComment()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Ana");
            var servicio = await SeedServicioAsync(context, "Corte", 100m);
            await SeedCobroAsync(context, funcionario.IdFuncionario, new DateTime(2026, 4, 5, 10, 0, 0), 100m, "EFECTIVO", servicioId: servicio.Id);

            var categoria = await SeedCategoriaAsync(context, "Operativo");
            await SeedEgresoAsync(context, categoria.Id, new DateTime(2026, 4, 15, 9, 0, 0), 200m);

            var sender = new FakeMonthlyReportEmailSender();
            var service = ControllerTestSupport.CreateMonthlyBusinessReportService(context, tenantProvider, sender);

            var report = await service.GenerateAsync(tenantId, 2026, 4);

            Assert.True(report.GananciaReal < 0);
            Assert.True(report.MargenGanancia < 0);
            Assert.Contains("pérdida", report.ComentarioMargen);
        }

        [Fact]
        public async Task GenerateAsync_ForAnotherTenant_Throws()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var sender = new FakeMonthlyReportEmailSender();
            var service = ControllerTestSupport.CreateMonthlyBusinessReportService(context, tenantProvider, sender);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.GenerateAsync(Guid.NewGuid(), 2026, 4));
        }

        [Theory]
        [InlineData(2026, 0)]
        [InlineData(2026, 13)]
        [InlineData(1999, 4)]
        public async Task GenerateAsync_InvalidPeriod_Throws(int year, int month)
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var sender = new FakeMonthlyReportEmailSender();
            var service = ControllerTestSupport.CreateMonthlyBusinessReportService(context, tenantProvider, sender);

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => service.GenerateAsync(tenantId, year, month));
        }

        // ─────────────── Envío de prueba ───────────────

        [Fact]
        public async Task SendTestAsync_RegistersTestLog_AndCanRepeat()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var sender = new FakeMonthlyReportEmailSender();
            var service = ControllerTestSupport.CreateMonthlyBusinessReportService(context, tenantProvider, sender);

            var first = await service.SendTestAsync(tenantId, 2026, 4, "duenio@negocio.cr", "user-1");
            var second = await service.SendTestAsync(tenantId, 2026, 4, "duenio@negocio.cr", "user-1");

            Assert.Equal(MonthlyReportSendOutcome.Sent, first.Outcome);
            Assert.Equal(MonthlyReportSendOutcome.Sent, second.Outcome);
            Assert.Equal(2, sender.Attempts.Count);

            var logs = await context.TenantMonthlyReportEmailLogs.AsNoTracking().ToListAsync();
            Assert.Equal(2, logs.Count);
            Assert.All(logs, log =>
            {
                Assert.True(log.IsTest);
                Assert.Equal(MonthlyReportEmailStatus.Sent, log.Status);
                Assert.Equal("duenio@negocio.cr", log.RecipientEmail);
                Assert.Equal("user-1", log.TriggeredByUserId);
                Assert.NotNull(log.ProviderMessageId);
                Assert.NotNull(log.SentAt);
                Assert.Equal(64, log.ContentHash!.Length);
                Assert.StartsWith("[Prueba]", log.Subject);
            });
        }

        [Fact]
        public async Task SendTestAsync_InvalidEmail_FailsWithoutSending()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var sender = new FakeMonthlyReportEmailSender();
            var service = ControllerTestSupport.CreateMonthlyBusinessReportService(context, tenantProvider, sender);

            var result = await service.SendTestAsync(tenantId, 2026, 4, "no-es-un-correo", "user-1");

            Assert.Equal(MonthlyReportSendOutcome.Failed, result.Outcome);
            Assert.Empty(sender.Attempts);
            Assert.Empty(await context.TenantMonthlyReportEmailLogs.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task SendTestAsync_ProviderFailure_LogsFailedWithError()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var sender = new FakeMonthlyReportEmailSender { Succeed = false };
            var service = ControllerTestSupport.CreateMonthlyBusinessReportService(context, tenantProvider, sender);

            var result = await service.SendTestAsync(tenantId, 2026, 4, "duenio@negocio.cr", "user-1");

            Assert.Equal(MonthlyReportSendOutcome.Failed, result.Outcome);

            var log = Assert.Single(await context.TenantMonthlyReportEmailLogs.AsNoTracking().ToListAsync());
            Assert.Equal(MonthlyReportEmailStatus.Failed, log.Status);
            Assert.Equal("Fallo simulado del proveedor.", log.ErrorMessage);
            Assert.Null(log.SentAt);
        }

        // ─────────────── Envío real ───────────────

        [Fact]
        public async Task SendMonthlyReportAsync_WithoutActiveConfiguration_Skips()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var sender = new FakeMonthlyReportEmailSender();
            var service = ControllerTestSupport.CreateMonthlyBusinessReportService(context, tenantProvider, sender);

            // Sin configuración guardada.
            var withoutSettings = await service.SendMonthlyReportAsync(tenantId, 2026, 4, "user-1");
            Assert.Equal(MonthlyReportSendOutcome.Skipped, withoutSettings.Outcome);

            // Con configuración guardada pero desactivada.
            context.TenantMonthlyReportSettings.Add(new TenantMonthlyReportSettings
            {
                TenantId = tenantId,
                IsEnabled = false,
                CreatedAt = new DateTime(2026, 4, 1),
                UpdatedAt = new DateTime(2026, 4, 1)
            });
            await context.SaveChangesAsync();

            var disabled = await service.SendMonthlyReportAsync(tenantId, 2026, 4, "user-1");
            Assert.Equal(MonthlyReportSendOutcome.Skipped, disabled.Outcome);

            Assert.Empty(sender.Attempts);
        }

        [Fact]
        public async Task SendMonthlyReportAsync_SendsToAdmins_NeverToFuncionarios()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedIdentityAsync(
                context,
                tenantId,
                adminEmail: "admin@negocio.cr",
                funcionarioEmail: "func@negocio.cr");

            context.TenantMonthlyReportSettings.Add(new TenantMonthlyReportSettings
            {
                TenantId = tenantId,
                IsEnabled = true,
                SendToOwnerEmail = true,
                // Incluye un correo válido, el correo del funcionario (debe excluirse),
                // un duplicado del admin y un correo inválido.
                AdditionalRecipients = "extra@negocio.cr, func@negocio.cr, ADMIN@negocio.cr, no-valido",
                CreatedAt = new DateTime(2026, 4, 1),
                UpdatedAt = new DateTime(2026, 4, 1)
            });
            await context.SaveChangesAsync();

            var sender = new FakeMonthlyReportEmailSender();
            var service = ControllerTestSupport.CreateMonthlyBusinessReportService(context, tenantProvider, sender);

            var result = await service.SendMonthlyReportAsync(tenantId, 2026, 4, "user-1");

            Assert.Equal(MonthlyReportSendOutcome.Sent, result.Outcome);
            Assert.Equal(2, result.SentCount);

            var recipients = sender.Attempts.Select(a => a.Recipient).OrderBy(r => r).ToList();
            Assert.Equal(new[] { "admin@negocio.cr", "extra@negocio.cr" }, recipients);
            Assert.DoesNotContain(sender.Attempts, a => a.Recipient.Contains("func@"));

            // El HTML enviado usa colones costarricenses (el símbolo va HTML-encoded).
            Assert.Contains(
                System.Text.Encodings.Web.HtmlEncoder.Default.Encode("₡"),
                sender.Attempts[0].HtmlBody);

            var logs = await context.TenantMonthlyReportEmailLogs.AsNoTracking().ToListAsync();
            Assert.Equal(2, logs.Count);
            Assert.All(logs, log =>
            {
                Assert.False(log.IsTest);
                Assert.Equal(MonthlyReportEmailStatus.Sent, log.Status);
            });
        }

        [Fact]
        public async Task SendMonthlyReportAsync_DuplicateRealSend_IsSkipped()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedIdentityAsync(context, tenantId, adminEmail: "admin@negocio.cr", funcionarioEmail: null);

            context.TenantMonthlyReportSettings.Add(new TenantMonthlyReportSettings
            {
                TenantId = tenantId,
                IsEnabled = true,
                SendToOwnerEmail = true,
                CreatedAt = new DateTime(2026, 4, 1),
                UpdatedAt = new DateTime(2026, 4, 1)
            });
            await context.SaveChangesAsync();

            var sender = new FakeMonthlyReportEmailSender();
            var service = ControllerTestSupport.CreateMonthlyBusinessReportService(context, tenantProvider, sender);

            var first = await service.SendMonthlyReportAsync(tenantId, 2026, 4, "user-1");
            var second = await service.SendMonthlyReportAsync(tenantId, 2026, 4, "user-1");

            Assert.Equal(MonthlyReportSendOutcome.Sent, first.Outcome);
            Assert.Equal(MonthlyReportSendOutcome.Skipped, second.Outcome);
            Assert.Equal(1, second.SkippedCount);

            // El proveedor solo recibió UN correo real.
            Assert.Single(sender.Attempts);

            var logs = await context.TenantMonthlyReportEmailLogs.AsNoTracking().ToListAsync();
            Assert.Equal(2, logs.Count);
            Assert.Equal(1, logs.Count(l => l.Status == MonthlyReportEmailStatus.Sent));
            Assert.Equal(1, logs.Count(l => l.Status == MonthlyReportEmailStatus.Skipped));

            // Otro mes del mismo tenant sí puede enviarse.
            var otherMonth = await service.SendMonthlyReportAsync(tenantId, 2026, 5, "user-1");
            Assert.Equal(MonthlyReportSendOutcome.Sent, otherMonth.Outcome);
        }

        [Fact]
        public async Task SendMonthlyReportAsync_ProviderFailure_LogsFailed()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedIdentityAsync(context, tenantId, adminEmail: "admin@negocio.cr", funcionarioEmail: null);

            context.TenantMonthlyReportSettings.Add(new TenantMonthlyReportSettings
            {
                TenantId = tenantId,
                IsEnabled = true,
                SendToOwnerEmail = true,
                CreatedAt = new DateTime(2026, 4, 1),
                UpdatedAt = new DateTime(2026, 4, 1)
            });
            await context.SaveChangesAsync();

            var sender = new FakeMonthlyReportEmailSender { Succeed = false };
            var service = ControllerTestSupport.CreateMonthlyBusinessReportService(context, tenantProvider, sender);

            var result = await service.SendMonthlyReportAsync(tenantId, 2026, 4, "user-1");

            Assert.Equal(MonthlyReportSendOutcome.Failed, result.Outcome);

            var log = Assert.Single(await context.TenantMonthlyReportEmailLogs.AsNoTracking().ToListAsync());
            Assert.Equal(MonthlyReportEmailStatus.Failed, log.Status);
            Assert.False(log.IsTest);
            Assert.NotNull(log.ErrorMessage);

            // Un fallo NO bloquea el reintento: al reintentar con proveedor sano, se envía.
            sender.Succeed = true;
            var retry = await service.SendMonthlyReportAsync(tenantId, 2026, 4, "user-1");
            Assert.Equal(MonthlyReportSendOutcome.Sent, retry.Outcome);
        }

        [Fact]
        public async Task SendMonthlyReportAsync_WithoutRecipients_Fails()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.TenantMonthlyReportSettings.Add(new TenantMonthlyReportSettings
            {
                TenantId = tenantId,
                IsEnabled = true,
                SendToOwnerEmail = true, // no hay usuarios admin sembrados
                AdditionalRecipients = null,
                CreatedAt = new DateTime(2026, 4, 1),
                UpdatedAt = new DateTime(2026, 4, 1)
            });
            await context.SaveChangesAsync();

            var sender = new FakeMonthlyReportEmailSender();
            var service = ControllerTestSupport.CreateMonthlyBusinessReportService(context, tenantProvider, sender);

            var result = await service.SendMonthlyReportAsync(tenantId, 2026, 4, "user-1");

            Assert.Equal(MonthlyReportSendOutcome.Failed, result.Outcome);
            Assert.Empty(sender.Attempts);
        }

        // ─────────────── Seeds ───────────────

        private static async Task SeedIdentityAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId,
            string adminEmail,
            string? funcionarioEmail)
        {
            // AppUsuario.TenantId tiene FK a Tenants: el tenant debe existir primero.
            context.Tenants.Add(new LuxuryApp.Models.SaaS.Tenant
            {
                Id = tenantId,
                Nombre = "Negocio Test"
            });

            var adminRole = new IdentityRole
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Administrador",
                NormalizedName = "ADMINISTRADOR"
            };
            var funcionarioRole = new IdentityRole
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Funcionario",
                NormalizedName = "FUNCIONARIO"
            };
            context.Roles.AddRange(adminRole, funcionarioRole);

            var admin = new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                UserName = adminEmail,
                NormalizedUserName = adminEmail.ToUpperInvariant(),
                Email = adminEmail,
                NormalizedEmail = adminEmail.ToUpperInvariant(),
                TenantId = tenantId,
                State = true
            };
            context.Users.Add(admin);
            context.UserRoles.Add(new IdentityUserRole<string> { UserId = admin.Id, RoleId = adminRole.Id });

            if (funcionarioEmail is not null)
            {
                var funcionarioUser = new AppUsuario
                {
                    Id = Guid.NewGuid().ToString(),
                    UserName = funcionarioEmail,
                    NormalizedUserName = funcionarioEmail.ToUpperInvariant(),
                    Email = funcionarioEmail,
                    NormalizedEmail = funcionarioEmail.ToUpperInvariant(),
                    TenantId = tenantId,
                    State = true,
                    FuncionarioId = 999
                };
                context.Users.Add(funcionarioUser);
                context.UserRoles.Add(new IdentityUserRole<string>
                {
                    UserId = funcionarioUser.Id,
                    RoleId = funcionarioRole.Id
                });
            }

            await context.SaveChangesAsync();
        }

        private static async Task<Funcionario> SeedFuncionarioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre)
        {
            var puesto = new Puesto
            {
                NombrePuesto = $"Puesto {nombre} {Guid.NewGuid():N}",
                Detalle = "Operativo",
                Activo = true
            };
            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            var funcionario = new Funcionario
            {
                Nombre = nombre,
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#123456",
                PorcentajeGanancia = 50m,
                PorcentajeProducto = 10m,
                FechaIngreso = new DateTime(2026, 1, 1),
                Activo = true
            };
            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }

        private static async Task<Servicio> SeedServicioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            decimal precio)
        {
            var servicio = new Servicio
            {
                Nombre = nombre,
                Precio = precio,
                DuracionMinutos = 45,
                Activo = true
            };
            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();
            return servicio;
        }

        private static async Task<Producto> SeedProductoAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            decimal precio)
        {
            var producto = new Producto
            {
                NombreProducto = nombre,
                PrecioProducto = precio,
                CantidadProducto = 5,
                Activo = true,
                FechaRegistro = new DateTime(2026, 1, 1)
            };
            context.Productos.Add(producto);
            await context.SaveChangesAsync();
            return producto;
        }

        private static async Task SeedCobroAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            int funcionarioId,
            DateTime fechaCobro,
            decimal monto,
            string metodoPago,
            int? servicioId = null,
            int? productoId = null)
        {
            context.Cobros.Add(new Cobro
            {
                FechaCobro = fechaCobro,
                NombreCliente = "Cliente prueba",
                FuncionarioId = funcionarioId,
                ServicioId = servicioId,
                ProductoId = productoId,
                Monto = monto,
                MetodoPago = metodoPago
            });
            await context.SaveChangesAsync();
        }

        private static async Task SeedCitaAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            int funcionarioId,
            DateTime fechaHora,
            int? servicioId = null)
        {
            context.Citas.Add(new Cita
            {
                FuncionarioId = funcionarioId,
                FechaHoraCita = fechaHora,
                NombreCliente = "Cliente cita",
                TelefonoCliente = "88888888",
                Tipo = "CITA",
                ServicioId = servicioId
            });
            await context.SaveChangesAsync();
        }

        private static async Task<Categoria> SeedCategoriaAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre)
        {
            var categoria = new Categoria
            {
                Nombre = nombre,
                Detalle = "Detalle",
                Activo = true
            };
            context.Categorias.Add(categoria);
            await context.SaveChangesAsync();
            return categoria;
        }

        private static async Task SeedEgresoAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            int categoriaId,
            DateTime fechaEgreso,
            decimal monto)
        {
            context.Egresos.Add(new Egreso
            {
                CategoriaId = categoriaId,
                FechaEgreso = fechaEgreso,
                Monto = monto,
                MetodoPago = "EFECTIVO",
                Detalle = "Egreso prueba"
            });
            await context.SaveChangesAsync();
        }
    }
}
