using AnimatorsWeb.Data;
using AnimatorsWeb.Helpers;
using AnimatorsWeb.Models;
using AnimatorsWeb.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

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

        [Route("/sitemap.xml")]
        public IActionResult SitemapXml()
        {
            string host = $"{Request.Scheme}://{Request.Host}";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">");
            sb.AppendLine($"  <url>");
            sb.AppendLine($"    <loc>{host}/</loc>");
            sb.AppendLine($"    <changefreq>weekly</changefreq>");
            sb.AppendLine($"    <priority>1.0</priority>");
            sb.AppendLine($"  </url>");
            sb.AppendLine("</urlset>");

            return Content(sb.ToString(), "application/xml", System.Text.Encoding.UTF8);
        }

        public async Task<IActionResult> Index()
        {
            var model = new LandingViewModel
            {
                Design = await _design.GetAsync(),
                GuildIconUrl = _design.GetLocalAvatarUrl() ?? _design.GetGuildIconUrl(),

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

            // ⬇⬇⬇ НОВОЕ: подтянуть всех соавторов для выбранных работ
            var chosenWorkUrls = chosen
                .Select(c => c.WorkUrl)
                .Where(u => !string.IsNullOrEmpty(u))
                .Distinct()
                .ToList();

            var authorsByUrl = new Dictionary<string, List<LandingAuthor>>();

            if (chosenWorkUrls.Count > 0)
            {
                var allAuthors = await _db.Participants
                    .Where(p => chosenWorkUrls.Contains(p.WorkUrl))
                    .Select(p => new { p.WorkUrl, p.DiscordUserId, p.DisplayName })
                    .ToListAsync();

                authorsByUrl = allAuthors
                    .GroupBy(p => p.WorkUrl)
                    .ToDictionary(
                        g => g.Key,
                        g => g.GroupBy(x => x.DiscordUserId)
                              .Select(x => x.First())
                              .Select(x => new LandingAuthor
                              {
                                  DisplayName = x.DisplayName,
                                  DiscordUserId = x.DiscordUserId
                              })
                              .ToList());
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

                // Авторы: все с этим WorkUrl, либо один текущий
                if (!string.IsNullOrEmpty(w.WorkUrl) && authorsByUrl.TryGetValue(w.WorkUrl, out var authors))
                {
                    item.Authors = authors;
                }
                else
                {
                    item.Authors.Add(new LandingAuthor
                    {
                        DisplayName = w.DisplayName
                    });
                }

                FillMedia(item);

                var local = _thumbService.GetUrl(item.ParticipantId);
                if (!string.IsNullOrEmpty(local))
                {
                    item.ThumbnailUrl = local;
                    if (item.MediaType == "video") item.MediaType = "image";
                }

                model.RecentWorks.Add(item);
            }

            return View(model);
        }

        private static void FillMedia(LandingWork item)
        {
            var ytId = YouTubeHelper.ExtractId(item.WorkUrl);
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