using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using AnimatorsWeb.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace AnimatorsWeb.Controllers
{
    public class HomeController : Controller
    {
        private readonly AppDbContext _db;
        private readonly DesignSettingsService _design;
        FeaturedThumbnailService _thumbService;
        private readonly IConfiguration _config;

        public HomeController(AppDbContext db, DesignSettingsService design, IConfiguration config, FeaturedThumbnailService thumb)
        {
            _db = db;
            _design = design;
            _config = config;
            _thumbService = thumb;
        }

        public async Task<IActionResult> Index()
        {
            var model = new LandingViewModel
            {
                Design = await _design.GetAsync(),
                GuildIconUrl = _design.GetLocalAvatarUrl() ?? _design.GetGuildIconUrl(),
                InviteUrl = _config["Discord:InviteUrl"] ?? "#",

                ParticipantsCount = await _db.Participants
                    .Select(p => p.DiscordUserId).Distinct().CountAsync(),

                AnimJamsCount = await _db.AnimJams.CountAsync(),

                WorksCount = await _db.Participants
                    .Where(p => !string.IsNullOrEmpty(p.WorkUrl))
                    .Select(p => p.WorkUrl).Distinct().CountAsync()
            };

            // Последние 9 уникальных работ
            var featuredOrder = await _db.LandingFeaturedWorks
                .OrderBy(f => f.Order)
                .Select(f => f.ParticipantId)
                .ToListAsync();

            List<(int Id, int AnimJamNumber, string WorkUrl, string MessageUrl, string DisplayName)> chosen;

            if (featuredOrder.Count > 0)
            {
                var parts = await _db.Participants
                    .Where(p => featuredOrder.Contains(p.Id))
                    .Select(p => new { p.Id, p.AnimJamNumber, p.WorkUrl, p.MessageUrl, p.DisplayName })
                    .ToListAsync();

                var byId = parts.ToDictionary(p => p.Id);
                chosen = new List<(int, int, string, string, string)>();
                foreach (var id in featuredOrder)
                {
                    if (!byId.TryGetValue(id, out var p)) continue;
                    chosen.Add((p.Id, p.AnimJamNumber, p.WorkUrl, p.MessageUrl, p.DisplayName));
                }
            }
            else
            {
                // Авто-выбор, как раньше
                var recent = await _db.Participants
                    .OrderByDescending(p => p.AnimJamNumber)
                    .Select(p => new { p.Id, p.AnimJamNumber, p.WorkUrl, p.MessageUrl, p.DisplayName })
                    .ToListAsync();

                chosen = recent
                    .Where(x => !string.IsNullOrEmpty(x.WorkUrl))
                    .GroupBy(x => x.WorkUrl)
                    .Select(g => g.First())
                    .OrderByDescending(x => x.AnimJamNumber)
                    .Take(9)
                    .Select(x => (x.Id, x.AnimJamNumber, x.WorkUrl, x.MessageUrl, x.DisplayName))
                    .ToList();
            }

            foreach (var w in chosen)
            {
                var item = new LandingWork
                {
                    ParticipantId = w.Id,
                    AnimJamNumber = w.AnimJamNumber,
                    WorkUrl = w.WorkUrl,
                    MessageUrl = w.MessageUrl,
                    DisplayName = w.DisplayName
                };
                FillMedia(item);

                var local = _thumbService.GetUrl(item.ParticipantId);
                if (!string.IsNullOrEmpty(local))
                {
                    item.ThumbnailUrl = local;
                    // если обложка есть — считаем её «image», даже если исходник видео
                    if (item.MediaType == "video") item.MediaType = "image";
                }

                model.RecentWorks.Add(item);
            }

            return View(model);
        }

        private static string? GetYouTubeId(string url)
        {
            var m = Regex.Match(url, @"(?:youtu\.be/|youtube\.com/(?:watch\?v=|embed/|v/))([a-zA-Z0-9_-]{11})");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static void FillMedia(LandingWork item)
        {
            var ytId = GetYouTubeId(item.WorkUrl);
            if (ytId != null)
            {
                item.MediaType = "youtube";
                item.ThumbnailUrl = $"https://img.youtube.com/vi/{ytId}/mqdefault.jpg";
            }
            else if (item.WorkUrl.Contains(".mp4", StringComparison.OrdinalIgnoreCase) ||
                     item.WorkUrl.Contains(".webm", StringComparison.OrdinalIgnoreCase) ||
                     item.WorkUrl.Contains(".mov", StringComparison.OrdinalIgnoreCase))
            {
                item.MediaType = "video";
                item.ThumbnailUrl = $"/Media/StreamWork/{item.ParticipantId}";
            }
            else if (Regex.IsMatch(item.WorkUrl, @"\.(gif|png|jpg|jpeg)(\?|$)", RegexOptions.IgnoreCase))
            {
                item.MediaType = "image";
                item.ThumbnailUrl = $"/Media/StreamWork/{item.ParticipantId}";
            }
            else
            {
                item.MediaType = "link";
            }
        }

    }
}