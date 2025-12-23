using Microsoft.AspNetCore.Mvc;
using audiobookzone.Models;
using System.Diagnostics;

namespace audiobookzone.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;

        public HomeController(ILogger<HomeController> logger)
        {
            _logger = logger;
        }

        public IActionResult Index()
        {
            // Check if user is logged in
            var username = HttpContext.Session.GetString("Username");
            if (string.IsNullOrEmpty(username))
            {
                return RedirectToAction("Login", "Account");
            }

            ViewBag.Username = username;
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            var exceptionHandlerPathFeature = HttpContext.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
            
            var errorViewModel = new ErrorViewModel 
            { 
                RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
                ExceptionMessage = exceptionHandlerPathFeature?.Error?.Message,
                StackTrace = exceptionHandlerPathFeature?.Error?.StackTrace,
                ExceptionType = exceptionHandlerPathFeature?.Error?.GetType().FullName
            };
            
            return View(errorViewModel);
        }
    }
}
