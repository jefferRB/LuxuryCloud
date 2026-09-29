using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Mensajes
{
    public class MensajesController : Controller
    {
        public IActionResult Index()
        {
            ViewData["FullScreen"] = "fullscreen-mensajes";
            return View();
        }
    }
}
