using QRCoder;

namespace LuxuryApp.Services.Reservas
{
    /// <summary>Imagen de QR lista para responder: bytes + content type + nombre sugerido.</summary>
    public sealed record BookingQrImage(byte[] Contenido, string ContentType, string NombreArchivo);

    /// <summary>
    /// Genera el código QR del enlace público de reservas.
    ///
    /// <para>
    /// El QR codifica ÚNICAMENTE la URL pública (<c>https://.../reservar/{slug}</c>): nunca el
    /// TenantId, ni tokens, ni ningún dato sensible. No se persiste: se deriva de la URL cada vez
    /// que se pide, así cambiar el slug lo regenera solo y no hay bitmaps guardados que envejezcan.
    /// </para>
    ///
    /// <para>
    /// La generación es local (QRCoder, sin System.Drawing): no depende de ningún servicio web
    /// externo ni de librerías nativas, así que funciona igual en el Linux de producción.
    /// </para>
    /// </summary>
    public interface IBookingQrCodeService
    {
        /// <summary>PNG para mostrar en pantalla y descargar.</summary>
        BookingQrImage CreatePng(string url, string slug);

        /// <summary>SVG vectorial, para imprimir en cualquier tamaño sin pixelarse.</summary>
        BookingQrImage CreateSvg(string url, string slug);
    }

    public sealed class BookingQrCodeService : IBookingQrCodeService
    {
        /// <summary>
        /// Corrección de errores media: tolera un QR algo sucio o impreso en poca calidad sin
        /// engordar el módulo tanto como los niveles altos.
        /// </summary>
        private const QRCodeGenerator.ECCLevel Correccion = QRCodeGenerator.ECCLevel.M;

        /// <summary>Píxeles por módulo del PNG. 10 da ~330 px para una URL corta: nítido en móvil.</summary>
        private const int PixelesPorModulo = 10;

        public BookingQrImage CreatePng(string url, string slug)
        {
            using var data = Generar(url);
            var png = new PngByteQRCode(data).GetGraphic(PixelesPorModulo);

            return new BookingQrImage(png, "image/png", $"reservas-{NombreSeguro(slug)}.png");
        }

        public BookingQrImage CreateSvg(string url, string slug)
        {
            using var data = Generar(url);
            var svg = new SvgQRCode(data).GetGraphic(PixelesPorModulo);

            return new BookingQrImage(
                System.Text.Encoding.UTF8.GetBytes(svg),
                "image/svg+xml",
                $"reservas-{NombreSeguro(slug)}.svg");
        }

        private static QRCodeData Generar(string url)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(url);

            using var generator = new QRCodeGenerator();
            return generator.CreateQrCode(url, Correccion);
        }

        /// <summary>
        /// Nombre de archivo defensivo: el slug ya viene normalizado, pero el nombre viaja en una
        /// cabecera HTTP y no se construye a partir de texto sin filtrar.
        /// </summary>
        private static string NombreSeguro(string? slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                return "luxurycloud";
            }

            var limpio = new string(slug.Where(caracter =>
                char.IsAsciiLetterOrDigit(caracter) || caracter == '-').ToArray());

            return string.IsNullOrEmpty(limpio) ? "luxurycloud" : limpio;
        }
    }
}
