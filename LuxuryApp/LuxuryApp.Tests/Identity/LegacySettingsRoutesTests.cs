using LuxuryApp.Controllers.Configuracion;
using LuxuryApp.Models.Identity;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Tests.Identity
{
    /// <summary>
    /// Qué pasó con las rutas viejas al simplificar el menú Opciones.
    ///
    /// <para>
    /// La regla es: una entrada de menú se puede quitar, una URL no. Quien tenga un favorito, un
    /// enlace en un correo o un botón dentro de otra pantalla debe seguir llegando a algún lado
    /// útil, nunca a un 404.
    /// </para>
    /// </summary>
    public class LegacySettingsRoutesTests
    {
        /// <summary>
        /// /ConfiguracionFiscal ya no tiene pantalla propia: manda a la sección equivalente de
        /// Mi cuenta. El destino es otro controlador, así que no hay ciclo de redirecciones.
        /// </summary>
        [Fact]
        public void ConfiguracionFiscalIndex_RedirigeAMiCuentaFiscal()
        {
            var controller = new ConfiguracionFiscalController(
                context: null!,
                tenantProvider: null!,
                accountSettingsPageBuilder: null!,
                logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfiguracionFiscalController>.Instance,
                impactoHistorico: null!);

            var resultado = Assert.IsType<RedirectToActionResult>(controller.Index());

            Assert.Equal("Cuenta", resultado.ActionName);
            Assert.Equal("Accounts", resultado.ControllerName);
            Assert.Equal(AccountSettingsSections.Fiscal, resultado.Fragment);

            // Redirigir a sí mismo sería un bucle infinito.
            Assert.NotEqual("ConfiguracionFiscal", resultado.ControllerName);
        }

        /// <summary>
        /// La vista suelta de impuestos se eliminó: si volviera a existir habría dos formularios
        /// para la misma configuración.
        /// </summary>
        [Fact]
        public void NoQuedaVistaSueltaDeImpuestos()
        {
            var vista = TestProjectPaths.ProjectPath("Views", "ConfiguracionFiscal", "Index.cshtml");
            Assert.False(File.Exists(vista), $"La vista suelta de impuestos debería haberse eliminado: {vista}");
        }

        /// <summary>
        /// /Seguridad/Enrolar NO se puede convertir en redirect: tiene dos consumidores que llegan
        /// directo. Si alguien la reemplaza por una redirección, el portal de funcionarios pierde
        /// su acceso a MFA y el enrolamiento obligatorio de superadmin entra en bucle.
        /// </summary>
        [Fact]
        public void EnrolarSigueSiendoUnaPantallaReal()
        {
            var vista = TestProjectPaths.ProjectPath("Views", "Seguridad", "Enrolar.cshtml");
            Assert.True(File.Exists(vista), "El enrolamiento de MFA debe seguir teniendo pantalla propia.");

            var contenido = File.ReadAllText(vista);
            Assert.Contains("mfa-qr", contenido, StringComparison.Ordinal);
            Assert.Contains("asp-action=\"Confirmar\"", contenido, StringComparison.Ordinal);

            // El portal de funcionarios sigue enlazando ahí.
            var layoutFuncionario = File.ReadAllText(
                TestProjectPaths.ProjectPath("Views", "Shared", "_FuncionarioLayout.cshtml"));
            Assert.Contains("/Seguridad/Enrolar", layoutFuncionario, StringComparison.Ordinal);

            // Y el filtro de enrolamiento obligatorio también.
            var filtro = File.ReadAllText(
                TestProjectPaths.ProjectPath("Filters", "RequireMfaEnrollmentFilter.cs"));
            Assert.Contains("\"Enrolar\", \"Seguridad\"", filtro, StringComparison.Ordinal);
        }

        /// <summary>
        /// Bloqueos de horario salió del menú, pero el módulo sigue vivo porque la configuración
        /// de Reservas enlaza Crear/Editar y sus POST redirigen a Index. Borrarlo habría roto esa
        /// pantalla; por eso sólo se quitó la entrada duplicada del menú.
        /// </summary>
        [Fact]
        public void BloqueosDeHorario_SigueTeniendoConsumidores()
        {
            foreach (var vista in new[] { "Index.cshtml", "Form.cshtml", "Detalle.cshtml" })
            {
                var path = TestProjectPaths.ProjectPath("Views", "BloqueosRecurrentes", vista);
                Assert.True(File.Exists(path), $"El módulo de bloqueos no debe eliminarse: falta {vista}.");
            }

            var configuracionReservas = File.ReadAllText(
                TestProjectPaths.ProjectPath("Views", "Reservas", "Configuracion.cshtml"));

            Assert.Contains("asp-controller=\"BloqueosRecurrentes\"", configuracionReservas, StringComparison.Ordinal);

            // El calendario sigue leyendo las ocurrencias de las reglas para pintar los descansos.
            var calendarJs = File.ReadAllText(
                TestProjectPaths.ProjectPath("wwwroot", "js", "calendar.js"));
            Assert.Contains("/Calendar/GetBloqueosRecurrentes", calendarJs, StringComparison.Ordinal);
        }
    }
}
