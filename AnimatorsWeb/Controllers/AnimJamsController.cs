using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using AnimatorsWeb.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace AnimatorsWeb.Controllers
{
    public class AnimJamsController : Controller
    {
        private readonly AppDbContext _db;

        public AnimJamsController(AppDbContext db)
        {
            _db = db;
        }

        public IActionResult Index()
        {
            return RedirectToAction("Index", "Events", new { tab = "anijam" });
        }
        public IActionResult Details(int number)
        {
            return RedirectPermanent($"/Events/Details/AniJam/{number}");
        }
        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            return url.TrimEnd('&', '?', ' ');
        }

    }
}