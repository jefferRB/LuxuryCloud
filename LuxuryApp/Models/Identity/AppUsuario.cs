using LuxuryApp.Models.Common;
using LuxuryApp.Models.SaaS;
using Microsoft.AspNetCore.Identity;

namespace LuxuryApp.Models.Identity
{
    public class AppUsuario : IdentityUser
    {
        public string? Name { get; set; }

        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public bool State { get; set; } = true;

        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public bool IsPlatformSuperAdmin { get; set; }

        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        public Tenant? Tenant { get; set; }

        /// <summary>
        /// Cuando esta cuenta es de acceso de un funcionario, apunta al
        /// <c>Funcionario.IdFuncionario</c> dentro del mismo tenant. Null para
        /// cuentas administrativas. Se usa para emitir el claim funcionario_id
        /// y aislar la información del portal de funcionarios.
        /// </summary>
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public int? FuncionarioId { get; set; }
    }
}
