using AnimatorsWeb.Models;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;

namespace AnimatorsWeb.Controllers
{
    public class HomeController : Controller
    {
        private readonly DiscordMessageService _discord;

        public HomeController(DiscordMessageService discord)
        {
            _discord = discord;
        }

        public IActionResult Index()
        {
            return View();
        }

        

        /*public IActionResult Privacy()
        {
            return View();
        }*/

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
