using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.DataBase
{
    public class ClienteVisitas : ITenantEntity
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        public int Id { get; set; }
        public int ClienteId { get; set; }

        [StringLength(50)]
        public string NumeroTelefono { get; set; } = string.Empty;

        public DateTime FechaVisita { get; set; }

        [ForeignKey(nameof(ClienteId))]
        public ClientesModel? Cliente { get; set; }
    }
}
