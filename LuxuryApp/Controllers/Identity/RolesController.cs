using LuxuryApp.Models.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Identity
{
    [Authorize(Roles = "Administrador")]
    public class RolesController : Controller
    {
        private readonly UserManager<AppUsuario> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;

        public RolesController(
            UserManager<AppUsuario> userManager,
            RoleManager<IdentityRole> roleManager)
        {
            _userManager = userManager;
            _roleManager = roleManager;
        }

        public IActionResult Index()
        {
            return View();
        }
    }
}
