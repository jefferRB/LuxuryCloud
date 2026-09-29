using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [Route("api/health/public-callback")]
    public class PublicCallbackHealthController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get() =>
            Content("OK LuxuryCloud public callback reachable", "text/plain");
    }
}
