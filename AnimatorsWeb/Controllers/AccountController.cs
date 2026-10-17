using AnimatorsWeb.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

public class AccountController : Controller
{
    private readonly AppDbContext _db;

    public AccountController(AppDbContext db) { _db = db; }

    public IActionResult Stats()
    {
        // Достаём Discord ID из claim
        string? discordId = User.FindFirst("discord_id")?.Value;

        if (discordId == null)
            return RedirectToAction("Login");

        // Здесь можно найти пользователя в БД по discordId и показать его статистику
        // Например: _db.Winners.Where(w => w.DiscordUserId == discordId)

        return View();
    }
    [HttpGet]
    public IActionResult Login(string returnUrl = "/")
    {
        return Challenge(new AuthenticationProperties { RedirectUri = returnUrl },
            "Discord");
    }

    [HttpGet]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return RedirectToAction("Index", "AnimJams");
    }
}