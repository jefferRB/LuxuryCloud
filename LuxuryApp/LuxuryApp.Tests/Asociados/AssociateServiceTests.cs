using LuxuryApp.Models.Asociados;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.Asociados;
using LuxuryApp.Services.Inversionistas;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Asociados
{
    /// <summary>
    /// Alta, edición y participación de asociados. Las reglas duras de dinero (100 %, inicio de
    /// periodo, versionado del acuerdo) siguen viviendo en el módulo de inversionistas y están
    /// cubiertas por <c>InvestorServiceTests</c>; acá se verifica que el módulo de Asociados las
    /// DELEGA en vez de reimplementarlas, y lo que es propio suyo.
    /// </summary>
    public sealed class AssociateServiceTests : IDisposable
    {
        private readonly Guid _tenantId = Guid.NewGuid();
        private readonly TestTenantProvider _tenantProvider;
        private readonly ApplicationDbContext _context;
        private readonly SqliteConnection _connection;
        private readonly FakePlatformAuditService _audit = new();
        private readonly TestHttpContextAccessor _accessor = TestHttpContextAccessor.Anonymous();

        public AssociateServiceTests()
        {
            _tenantProvider = new TestTenantProvider { TenantId = _tenantId };
            (_context, _connection) = TestDbContextFactory.CreateSqliteContext(_tenantProvider);
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        [Fact]
        public async Task Crear_InversionistaSinAcceso_NoNecesitaCuentaIdentity()
        {
            var service = CreateService();

            var id = await service.CreateAsync(
                new AssociateFormViewModel
                {
                    Nombre = "Jairo",
                    Email = "jairo@test.local",
                    Puesto = "Socio",
                    Tipos = [AssociateType.Inversionista, AssociateType.Socio],
                    DarAcceso = false,
                    ParticipacionPorcentaje = 40m,
                    EffectiveFrom = new DateTime(2026, 1, 1),
                    Frecuencia = InvestorPayoutFrequency.Mensual
                },
                actorUserId: "admin-1");

            var asociado = await _context.Associates
                .Include(current => current.Tipos)
                .SingleAsync(current => current.Id == id);

            // Existe, participa, y NO tiene credenciales: es el caso normal de un inversionista.
            Assert.Null(asociado.AppUsuarioId);
            Assert.False(asociado.TieneAcceso);
            Assert.Empty(_context.Users);
            Assert.Equal(2, asociado.Tipos.Count);

            var perfil = await _context.TenantInvestors
                .Include(investor => investor.Acuerdos)
                .SingleAsync();

            Assert.Equal(id, perfil.AssociateId);
            Assert.Equal(40m, perfil.Acuerdos.Single().ParticipacionPorcentaje);
        }

        [Fact]
        public async Task Crear_NoInversionistaSinCorreo_EsPermitido()
        {
            var service = CreateService();

            var id = await service.CreateAsync(
                new AssociateFormViewModel
                {
                    Nombre = "Contador externo",
                    Tipos = [AssociateType.Contabilidad],
                    DarAcceso = false
                },
                actorUserId: "admin-1");

            var asociado = await _context.Associates.SingleAsync(current => current.Id == id);
            Assert.Null(asociado.Email);
        }

        [Fact]
        public async Task Crear_InversionistaSinCorreo_EsRechazado()
        {
            var service = CreateService();

            var ex = await Assert.ThrowsAsync<AssociateValidationException>(() =>
                service.CreateAsync(
                    new AssociateFormViewModel
                    {
                        Nombre = "Jairo",
                        Tipos = [AssociateType.Inversionista],
                        ParticipacionPorcentaje = 40m,
                        EffectiveFrom = new DateTime(2026, 1, 1)
                    },
                    actorUserId: "admin-1"));

            Assert.Contains("correo", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(_context.Associates);
        }

        [Fact]
        public async Task Crear_SinTipo_EsRechazado()
        {
            var service = CreateService();

            await Assert.ThrowsAsync<AssociateValidationException>(() =>
                service.CreateAsync(
                    new AssociateFormViewModel { Nombre = "Sin tipo", Tipos = [] },
                    actorUserId: "admin-1"));
        }

        [Fact]
        public async Task Crear_ConCorreoDuplicado_EsRechazado()
        {
            var service = CreateService();

            await service.CreateAsync(
                new AssociateFormViewModel
                {
                    Nombre = "Andrea",
                    Email = "andrea@test.local",
                    Tipos = [AssociateType.Marketing]
                },
                actorUserId: "admin-1");

            await Assert.ThrowsAsync<AssociateValidationException>(() =>
                service.CreateAsync(
                    new AssociateFormViewModel
                    {
                        Nombre = "Otra Andrea",
                        Email = "ANDREA@test.local",
                        Tipos = [AssociateType.Marketing]
                    },
                    actorUserId: "admin-1"));
        }

        [Fact]
        public async Task Participacion_QueSuperaEl100_EsRechazada()
        {
            var service = CreateService();

            await service.CreateAsync(
                new AssociateFormViewModel
                {
                    Nombre = "Jairo",
                    Email = "jairo@test.local",
                    Tipos = [AssociateType.Inversionista],
                    ParticipacionPorcentaje = 60m,
                    EffectiveFrom = new DateTime(2026, 1, 1)
                },
                actorUserId: "admin-1");

            // La regla de 100 % la aplica InvestorService: el módulo de Asociados la delega.
            var ex = await Assert.ThrowsAsync<InvestorValidationException>(() =>
                service.CreateAsync(
                    new AssociateFormViewModel
                    {
                        Nombre = "Carlos",
                        Email = "carlos@test.local",
                        Tipos = [AssociateType.Inversionista],
                        ParticipacionPorcentaje = 50m,
                        EffectiveFrom = new DateTime(2026, 1, 1)
                    },
                    actorUserId: "admin-1"));

            Assert.Contains("110", ex.Message);
            Assert.Single(_context.TenantInvestors);
        }

        [Fact]
        public async Task Participacion_DeVariosAsociados_SumaEnElListado()
        {
            var service = CreateService();

            await service.CreateAsync(
                new AssociateFormViewModel
                {
                    Nombre = "Jairo",
                    Email = "jairo@test.local",
                    Tipos = [AssociateType.Inversionista],
                    ParticipacionPorcentaje = 40m,
                    EffectiveFrom = new DateTime(2026, 1, 1)
                },
                actorUserId: "admin-1");

            await service.CreateAsync(
                new AssociateFormViewModel
                {
                    Nombre = "Pedro",
                    Email = "pedro@test.local",
                    Tipos = [AssociateType.Inversionista],
                    ParticipacionPorcentaje = 10m,
                    EffectiveFrom = new DateTime(2026, 1, 1)
                },
                actorUserId: "admin-1");

            var index = await service.BuildIndexAsync(puedeAdministrar: true);

            Assert.Equal(50m, index.ParticipacionAsignada);
            Assert.Equal(2, index.TotalActivos);
            Assert.Equal(0, index.TotalConAcceso);
        }

        [Fact]
        public async Task CambioDePorcentaje_CierraLaVersionAnteriorYConservaElHistorial()
        {
            var service = CreateService();

            var id = await service.CreateAsync(
                new AssociateFormViewModel
                {
                    Nombre = "Jairo",
                    Email = "jairo@test.local",
                    Tipos = [AssociateType.Inversionista],
                    ParticipacionPorcentaje = 40m,
                    EffectiveFrom = new DateTime(2026, 1, 1)
                },
                actorUserId: "admin-1");

            await service.SaveParticipationAsync(
                new AssociateParticipationFormViewModel
                {
                    AssociateId = id,
                    ParticipacionPorcentaje = 35m,
                    EffectiveFrom = new DateTime(2026, 8, 1),
                    Frecuencia = InvestorPayoutFrequency.Mensual
                },
                actorUserId: "admin-1");

            var acuerdos = await _context.InvestorAgreements
                .OrderBy(agreement => agreement.EffectiveFrom)
                .ToListAsync();

            // El acuerdo viejo NO se reescribió: se cerró. Así un estado de cuenta de julio sigue
            // explicando con qué porcentaje se calculó.
            Assert.Equal(2, acuerdos.Count);
            Assert.Equal(40m, acuerdos[0].ParticipacionPorcentaje);
            Assert.Equal(new DateOnly(2026, 7, 31), acuerdos[0].EffectiveTo);
            Assert.Equal(35m, acuerdos[1].ParticipacionPorcentaje);
            Assert.Null(acuerdos[1].EffectiveTo);
        }

        [Fact]
        public async Task Desactivar_ConservaParticipacionEHistorial()
        {
            var service = CreateService();

            var id = await service.CreateAsync(
                new AssociateFormViewModel
                {
                    Nombre = "Jairo",
                    Email = "jairo@test.local",
                    Tipos = [AssociateType.Inversionista],
                    ParticipacionPorcentaje = 40m,
                    EffectiveFrom = new DateTime(2026, 1, 1)
                },
                actorUserId: "admin-1");

            await service.SetActivoAsync(id, activo: false, actorUserId: "admin-1");

            var asociado = await _context.Associates.SingleAsync(current => current.Id == id);
            var perfil = await _context.TenantInvestors.Include(i => i.Acuerdos).SingleAsync();

            Assert.False(asociado.Activo);
            Assert.False(perfil.Activo);

            // Nada se borró: el acuerdo sigue ahí con su porcentaje y sus fechas.
            Assert.Single(perfil.Acuerdos);
            Assert.Equal(40m, perfil.Acuerdos.Single().ParticipacionPorcentaje);

            // Y deja de contar para el reparto (el acuerdo de un inactivo no ocupa porcentaje).
            var index = await service.BuildIndexAsync(puedeAdministrar: true);
            Assert.Equal(0m, index.ParticipacionAsignada);
        }

        [Fact]
        public async Task QuitarTipoInversionista_ConParticipacionActiva_EsRechazado()
        {
            var service = CreateService();

            var id = await service.CreateAsync(
                new AssociateFormViewModel
                {
                    Nombre = "Jairo",
                    Email = "jairo@test.local",
                    Tipos = [AssociateType.Inversionista],
                    ParticipacionPorcentaje = 40m,
                    EffectiveFrom = new DateTime(2026, 1, 1)
                },
                actorUserId: "admin-1");

            var ex = await Assert.ThrowsAsync<AssociateValidationException>(() =>
                service.UpdateAsync(
                    id,
                    new AssociateFormViewModel
                    {
                        Nombre = "Jairo",
                        Email = "jairo@test.local",
                        Tipos = [AssociateType.Socio]
                    },
                    actorUserId: "admin-1"));

            Assert.Contains("participación activa", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Editar_SincronizaElPerfilDeInversionista()
        {
            var service = CreateService();

            var id = await service.CreateAsync(
                new AssociateFormViewModel
                {
                    Nombre = "Jairo",
                    Email = "jairo@test.local",
                    Tipos = [AssociateType.Inversionista],
                    ParticipacionPorcentaje = 40m,
                    EffectiveFrom = new DateTime(2026, 1, 1)
                },
                actorUserId: "admin-1");

            await service.UpdateAsync(
                id,
                new AssociateFormViewModel
                {
                    Nombre = "Jairo Rodríguez",
                    Email = "jairo.nuevo@test.local",
                    Tipos = [AssociateType.Inversionista]
                },
                actorUserId: "admin-1");

            var perfil = await _context.TenantInvestors.SingleAsync();

            // Una sola verdad para el nombre y el correo que salen en el estado de cuenta.
            Assert.Equal("Jairo Rodríguez", perfil.Nombre);
            Assert.Equal("jairo.nuevo@test.local", perfil.Email);
        }

        [Fact]
        public async Task Asociados_NoSeVenEntreTenants()
        {
            var service = CreateService();

            await service.CreateAsync(
                new AssociateFormViewModel
                {
                    Nombre = "Jairo del negocio A",
                    Email = "jairo@test.local",
                    Tipos = [AssociateType.Inversionista],
                    ParticipacionPorcentaje = 40m,
                    EffectiveFrom = new DateTime(2026, 1, 1)
                },
                actorUserId: "admin-1");

            var otroTenant = Guid.NewGuid();
            _tenantProvider.TenantId = otroTenant;

            var indexOtroNegocio = await CreateService().BuildIndexAsync(puedeAdministrar: true);

            Assert.Empty(indexOtroNegocio.Asociados);
            Assert.Equal(0m, indexOtroNegocio.ParticipacionAsignada);

            // Y tampoco puede abrir el detalle de un asociado ajeno conociendo su Id.
            var asociadoAjeno = await CreateService().BuildDetailAsync(1, puedeAdministrar: true);
            Assert.Null(asociadoAjeno);
        }

        private AssociateService CreateService()
        {
            var permissionService = AssociateTestSupport.CreatePermissionService(_context, _accessor, _audit);
            var investorService = InvestorTestSupport.CreateInvestorService(_context, _audit);

            var accessService = new AssociateAccessService(
                _context,
                AssociateTestSupport.CreateUserManager(_context),
                new LuxuryApp.Services.Identity.TenantAccountProvisioningService(
                    _context,
                    AssociateTestSupport.CreateUserManager(_context),
                    Microsoft.Extensions.Logging.Abstractions
                        .NullLogger<LuxuryApp.Services.Identity.TenantAccountProvisioningService>.Instance),
                permissionService,
                _audit,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AssociateAccessService>.Instance);

            return AssociateTestSupport.CreateAssociateService(
                _context,
                _accessor,
                _audit,
                accessService,
                _tenantProvider,
                investorService);
        }
    }
}
