using LuxuryApp.Models.PublicPages;

namespace LuxuryApp.Services.PublicImages
{
    /// <summary>
    /// Resuelve el perfil de procesamiento segun el uso de la imagen. Lo consumen el servicio
    /// de subida (procesamiento) y la vista de configuracion (opciones del cropper), de forma
    /// que exista una sola definicion de las reglas por uso.
    /// </summary>
    public interface IPublicImageProfileProvider
    {
        PublicImageProfile Get(TenantPublicAssetType usage);
    }
}
