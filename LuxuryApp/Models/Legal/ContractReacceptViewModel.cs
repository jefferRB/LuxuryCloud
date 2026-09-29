using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Legal
{
    public sealed class ContractReacceptViewModel
    {
        public bool HasActiveDocument { get; set; }

        public Guid ContractDocumentId { get; set; }


        public string Title { get; set; } = string.Empty;


        public string VersionNumber { get; set; } = string.Empty;

        public DateTime? EffectiveFromUtc { get; set; }


        public string ContentHtml { get; set; } = string.Empty;

        public string ReturnUrl { get; set; } = "/";

        [Range(typeof(bool), "true", "true", ErrorMessage = "Debes aceptar el contrato vigente para continuar.")]
        public bool AcceptCurrentContract { get; set; }
    }
}
