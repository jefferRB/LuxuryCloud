using LuxuryApp.Tests.Support;

namespace LuxuryApp.Tests.Apariencia
{
    /// <summary>
    /// Unificación visual fase 2: Mi cuenta, Suscripción, WhatsApp, la verificación en dos pasos y
    /// Reservas deben usar el MISMO contenedor de módulo.
    ///
    /// <para>
    /// Son pruebas de la vista (texto del .cshtml/.css), no de píxeles: lo que garantizan es que
    /// nadie vuelva a inventar un shell propio con otro ancho, otro radio y otra sombra, que es
    /// justamente lo que hacía que cada pantalla de configuración pareciera otra aplicación.
    /// </para>
    /// </summary>
    public class ModuleShellConsistencyTests
    {
        public static TheoryData<string[]> PantallasDelShell => new()
        {
            new[] { "Views", "Accounts", "Cuenta.cshtml" },
            new[] { "Views", "Billing", "Suscripcion.cshtml" },
            new[] { "Views", "WhatsApp", "Index.cshtml" },
            new[] { "Views", "Seguridad", "Enrolar.cshtml" },
            new[] { "Views", "Reservas", "Configuracion.cshtml" }
        };

        [Theory]
        [MemberData(nameof(PantallasDelShell))]
        public void PantallasUnificadas_UsanElMismoShellYCargan_financeModules(string[] ruta)
        {
            var vista = Leer(ruta);

            Assert.Contains("finance-page-shell", vista, StringComparison.Ordinal);
            Assert.Contains("finance-modules.css", vista, StringComparison.Ordinal);
            Assert.Contains("finance-hero", vista, StringComparison.Ordinal);
        }

        /// <summary>
        /// Ninguna de estas pantallas puede fijar su propio ancho: el ancho lo da el
        /// <c>.container</c> del layout privado, igual para todos los módulos.
        /// </summary>
        [Theory]
        [MemberData(nameof(PantallasDelShell))]
        public void PantallasUnificadas_NoHardcodeanAnchoNiEstilosInline(string[] ruta)
        {
            var vista = Leer(ruta);

            Assert.DoesNotContain("max-width:", vista, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<style", vista, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Las primitivas de sección viven UNA vez, en finance-modules.css, con los alias .rsv-*
        /// que usa Reservas. Si alguien las vuelve a declarar en el CSS del módulo, vuelven a
        /// divergir.
        /// </summary>
        [Fact]
        public void PrimitivasDeSeccion_SeDefinenUnaSolaVez()
        {
            var compartido = LeerCss("finance-modules.css");

            Assert.Contains(".finance-section,", compartido, StringComparison.Ordinal);
            Assert.Contains(".rsv-section {", compartido, StringComparison.Ordinal);

            // Reservas ya no las redefine.
            var reservas = LeerCss("reservas-admin.css");
            Assert.DoesNotContain(".rsv-section {", reservas, StringComparison.Ordinal);
            Assert.DoesNotContain(".rsv-band {", reservas, StringComparison.Ordinal);
        }

        /// <summary>
        /// Un solo interruptor: la geometría se declara en finance-modules.css y WhatsApp sólo
        /// cambia el color de "encendido" (verde = se envían mensajes).
        /// </summary>
        [Fact]
        public void Interruptores_CompartenGeometria()
        {
            var compartido = LeerCss("finance-modules.css");
            Assert.Contains(".finance-switch,", compartido, StringComparison.Ordinal);
            Assert.Contains(".wa-switch {", compartido, StringComparison.Ordinal);

            // WhatsApp NO vuelve a declarar la geometría (la regla suelta al inicio de línea);
            // sólo conserva el selector de estado "encendido".
            var whatsapp = LeerCss("whatsapp-settings.css")
                .Replace("\r\n", "\n", StringComparison.Ordinal);

            Assert.DoesNotContain("\n.wa-switch-track {", whatsapp, StringComparison.Ordinal);
            Assert.DoesNotContain("\n.wa-switch {", whatsapp, StringComparison.Ordinal);
            Assert.Contains(".wa-switch input:checked ~ .wa-switch-track", whatsapp, StringComparison.Ordinal);
        }

        // ── Mi cuenta ──

        /// <summary>
        /// La unificación es visual, NO de backend: cada sección conserva su formulario, su acción
        /// y su antiforgery. Un mega-form que guardara perfil + IVA a la vez es exactamente lo que
        /// no se quiere.
        /// </summary>
        [Fact]
        public void MiCuenta_TieneUnFormularioPorResponsabilidad()
        {
            var perfil = Leer("Views", "Accounts", "_AccountProfile.cshtml");
            var seguridad = Leer("Views", "Accounts", "_AccountSecurity.cshtml");
            var fiscal = Leer("Views", "Accounts", "_AccountFiscalSettings.cshtml");

            // Perfil → Accounts/Cuenta
            Assert.Contains("asp-controller=\"Accounts\" asp-action=\"Cuenta\"", perfil, StringComparison.Ordinal);

            // Fiscal → ConfiguracionFiscal/Index (el endpoint de siempre, con su autorización)
            Assert.Contains("asp-controller=\"ConfiguracionFiscal\" asp-action=\"Index\"", fiscal, StringComparison.Ordinal);

            // Seguridad → los endpoints que ya existían; acá no se reimplementa 2FA.
            Assert.Contains("asp-action=\"EnviarEnlaceCambioPassword\"", seguridad, StringComparison.Ordinal);
            Assert.Contains("asp-action=\"Deshabilitar\"", seguridad, StringComparison.Ordinal);
            Assert.Contains("asp-action=\"CerrarSesionEnTodosLosDispositivos\"", seguridad, StringComparison.Ordinal);
            Assert.Contains("asp-action=\"Enrolar\"", seguridad, StringComparison.Ordinal);

            // Todos los formularios llevan antiforgery.
            foreach (var partial in new[] { perfil, seguridad, fiscal })
            {
                Assert.Contains("@Html.AntiForgeryToken()", partial, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// El secreto TOTP y el QR NO pasan por Mi cuenta: esa pantalla sólo muestra el estado y
        /// enlaza al enrolamiento. Menos superficies con el secreto, mejor.
        /// </summary>
        [Fact]
        public void MiCuenta_NoExponeElSecretoTotp()
        {
            foreach (var vista in new[]
            {
                Leer("Views", "Accounts", "Cuenta.cshtml"),
                Leer("Views", "Accounts", "_AccountSecurity.cshtml")
            })
            {
                Assert.DoesNotContain("OtpauthUri", vista, StringComparison.Ordinal);
                Assert.DoesNotContain("ClaveFormateada", vista, StringComparison.Ordinal);
                Assert.DoesNotContain("otpauth://", vista, StringComparison.Ordinal);
            }
        }

        /// <summary>El estado no se comunica sólo con color: también con texto.</summary>
        [Fact]
        public void MiCuenta_DiceElEstadoDeSeguridadConTexto()
        {
            var seguridad = Leer("Views", "Accounts", "_AccountSecurity.cshtml");

            Assert.Contains("Protegida", seguridad, StringComparison.Ordinal);
            Assert.Contains("No activada", seguridad, StringComparison.Ordinal);
            Assert.Contains("Activar verificación en dos pasos", seguridad, StringComparison.Ordinal);
        }

        /// <summary>El aviso fiscal sigue visible y no es descartable.</summary>
        [Fact]
        public void MiCuenta_ConservaElAvisoFiscal()
        {
            var fiscal = Leer("Views", "Accounts", "_AccountFiscalSettings.cshtml");

            Assert.Contains("a futuro", fiscal, StringComparison.Ordinal);
            Assert.Contains("conservan sus montos históricos", fiscal, StringComparison.Ordinal);
            Assert.Contains("_AvisoImpactoHistorico", fiscal, StringComparison.Ordinal);
            Assert.DoesNotContain("data-bs-dismiss", fiscal, StringComparison.Ordinal);
        }

        // ── Suscripción y WhatsApp ──

        /// <summary>
        /// Envolver Suscripción en la card estándar no puede haberse llevado por delante ninguna
        /// pieza comercial: estado, resumen, selector de plan, add-ons y código promocional.
        /// </summary>
        [Fact]
        public void Suscripcion_ConservaSusSeccionesYAnclas()
        {
            var vista = Leer("Views", "Billing", "Suscripcion.cshtml");

            Assert.Contains("Estado actual", vista, StringComparison.Ordinal);
            Assert.Contains("Resumen de tu cuenta", vista, StringComparison.Ordinal);
            Assert.Contains("Elegí tu plan", vista, StringComparison.Ordinal);
            Assert.Contains("Paquetes WhatsApp", vista, StringComparison.Ordinal);
            Assert.Contains("Código promocional", vista, StringComparison.Ordinal);

            // Las anclas son contrato: se enlazan desde esta vista y desde WhatsApp.
            Assert.Contains("id=\"planes\"", vista, StringComparison.Ordinal);
            Assert.Contains("id=\"paquetes\"", vista, StringComparison.Ordinal);

            // Y las piezas que la mueven siguen ahí.
            Assert.Contains("_SubscriptionCalculator", vista, StringComparison.Ordinal);
            Assert.Contains("_WhatsAppPackageCards", vista, StringComparison.Ordinal);
            Assert.Contains("AplicarCodigoPromocional", vista, StringComparison.Ordinal);
            Assert.Contains("CancelarWhatsAppAddon", vista, StringComparison.Ordinal);
        }

        [Fact]
        public void WhatsApp_ConservaEstadoAutomatizacionesYPaquetes()
        {
            var vista = Leer("Views", "WhatsApp", "Index.cshtml");

            Assert.Contains("Estado del add-on", vista, StringComparison.Ordinal);
            Assert.Contains("Automatizaciones", vista, StringComparison.Ordinal);
            Assert.Contains("Paquetes disponibles", vista, StringComparison.Ordinal);

            // Estado vacío conservado tal cual.
            Assert.Contains("No tenés un paquete de WhatsApp activo.", vista, StringComparison.Ordinal);

            // El motor sigue siendo el mismo: el partial de automatizaciones y las cards de paquetes.
            Assert.Contains("_AutomationSettings", vista, StringComparison.Ordinal);
            Assert.Contains("_WhatsAppPackageCards", vista, StringComparison.Ordinal);
            Assert.Contains("id=\"paquetes\"", vista, StringComparison.Ordinal);

            // El consumo sigue saliendo del summary, no de números fijos en la vista.
            Assert.Contains("WhatsAppMessagesRemaining", vista, StringComparison.Ordinal);
            Assert.Contains("WhatsAppDailyLimit", vista, StringComparison.Ordinal);
        }

        // ── Integridad estructural del markup ──

        public static TheoryData<string[]> VistasReestructuradas => new()
        {
            new[] { "Views", "Accounts", "Cuenta.cshtml" },
            new[] { "Views", "Accounts", "_AccountProfile.cshtml" },
            new[] { "Views", "Accounts", "_AccountSecurity.cshtml" },
            new[] { "Views", "Accounts", "_AccountPreferences.cshtml" },
            new[] { "Views", "Accounts", "_AccountFiscalSettings.cshtml" },
            new[] { "Views", "Billing", "Suscripcion.cshtml" },
            new[] { "Views", "WhatsApp", "Index.cshtml" },
            new[] { "Views", "Seguridad", "Enrolar.cshtml" }
        };

        /// <summary>
        /// Envolver pantallas enteras en un contenedor nuevo es donde se rompen las etiquetas.
        /// En estas vistas las secciones y los formularios están FUERA de los condicionales, así
        /// que las aperturas y los cierres deben cuadrar exactamente.
        /// </summary>
        [Theory]
        [MemberData(nameof(VistasReestructuradas))]
        public void VistasReestructuradas_TienenEtiquetasBalanceadas(string[] ruta)
        {
            var vista = Leer(ruta);

            foreach (var etiqueta in new[] { "section", "div", "form" })
            {
                var aperturas = Contar(vista, "<" + etiqueta);
                var cierres = Contar(vista, "</" + etiqueta + ">");

                Assert.True(
                    aperturas == cierres,
                    $"{string.Join('/', ruta)}: <{etiqueta}> abre {aperturas} veces y cierra {cierres}.");
            }
        }

        /// <summary>
        /// Un formulario dentro de otro es HTML inválido y el navegador descarta el interno: el
        /// botón deja de enviar. Con cuatro secciones en una sola pantalla es el error fácil.
        /// </summary>
        [Fact]
        public void MiCuenta_NoAnidaFormularios()
        {
            // La vista contenedora no abre ningún formulario: los abren las secciones.
            var contenedora = Leer("Views", "Accounts", "Cuenta.cshtml");
            Assert.Equal(0, Contar(contenedora, "<form"));

            // Y cada sección abre exactamente los suyos, sin anidar.
            foreach (var parcial in new[]
            {
                "_AccountProfile.cshtml", "_AccountSecurity.cshtml",
                "_AccountPreferences.cshtml", "_AccountFiscalSettings.cshtml"
            })
            {
                var vista = Leer("Views", "Accounts", parcial);
                var sinFormularios = System.Text.RegularExpressions.Regex.Replace(
                    vista,
                    @"<form\b[^>]*>((?!<form\b)[\s\S])*?</form>",
                    string.Empty,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                Assert.Equal(0, Contar(sinFormularios, "<form"));
            }
        }

        /// <summary>
        /// Las anclas de las secciones son únicas dentro de la página: son a la vez destino de
        /// las rutas legacy y objetivo del scroll.
        /// </summary>
        [Fact]
        public void MiCuenta_TieneAnclasUnicas()
        {
            var html = string.Concat(
                Leer("Views", "Accounts", "Cuenta.cshtml"),
                Leer("Views", "Accounts", "_AccountProfile.cshtml"),
                Leer("Views", "Accounts", "_AccountSecurity.cshtml"),
                Leer("Views", "Accounts", "_AccountPreferences.cshtml"),
                Leer("Views", "Accounts", "_AccountFiscalSettings.cshtml"));

            foreach (var seccion in new[] { "Perfil", "Seguridad", "Preferencias", "Fiscal" })
            {
                Assert.Equal(1, Contar(html, "AccountSettingsSections." + seccion + "\">"));
            }

            // Y los id de los formularios/botones no se repiten entre secciones.
            foreach (var id in new[]
            {
                "formCuenta", "formEnlace", "formFiscal",
                "btnGuardarCambios", "btnEnviarEnlace", "btnGuardarFiscal"
            })
            {
                Assert.Equal(1, Contar(html, "id=\"" + id + "\""));
            }
        }

        private static int Contar(string texto, string aguja)
        {
            var total = 0;
            var indice = texto.IndexOf(aguja, StringComparison.OrdinalIgnoreCase);

            while (indice >= 0)
            {
                total++;
                indice = texto.IndexOf(aguja, indice + aguja.Length, StringComparison.OrdinalIgnoreCase);
            }

            return total;
        }

        // ── Helpers ──

        private static string Leer(params string[] ruta)
        {
            var path = TestProjectPaths.ProjectPath(ruta);
            Assert.True(File.Exists(path), $"No se encontró la vista: {path}");
            return File.ReadAllText(path);
        }

        private static string LeerCss(string archivo)
        {
            var path = TestProjectPaths.ProjectPath("wwwroot", "css", archivo);
            Assert.True(File.Exists(path), $"No se encontró el CSS: {path}");
            return File.ReadAllText(path);
        }
    }
}
