using AnimatorsWeb.Data;
using AnimatorsWeb.Helpers;
using AnimatorsWeb.Models;
using AnimatorsWeb.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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

            List<(int Id, int AnimJamNumber, string WorkUrl, string MessageUrl, string DisplayName, int AttachmentCount)> chosen;

            if (featuredOrder.Count > 0)
            {
                var parts = await _db.Participants
                    .Where(p => featuredOrder.Contains(p.Id))
                    .Select(p => new { p.Id, p.AnimJamNumber, p.WorkUrl, p.MessageUrl, p.DisplayName, p.AttachmentCount })
                    .ToListAsync();

                var byId = parts.ToDictionary(p => p.Id);
                chosen = new List<(int, int, string, string, string,int)>();
                foreach (var id in featuredOrder)
                {
                    if (!byId.TryGetValue(id, out var p)) continue;
                    chosen.Add((p.Id, p.AnimJamNumber, p.WorkUrl, p.MessageUrl, p.DisplayName, p.AttachmentCount));
                }
            }
            else
            {
                var recent = await _db.Participants
                    .OrderByDescending(p => p.AnimJamNumber)
                    .Select(p => new { p.Id, p.AnimJamNumber, p.WorkUrl, p.MessageUrl, p.DisplayName, p.AttachmentCount })
                    .ToListAsync();

                chosen = recent
                    .Where(x => !string.IsNullOrEmpty(x.WorkUrl))
                    .GroupBy(x => x.WorkUrl)
                    .Select(g => g.First())
                    .OrderByDescending(x => x.AnimJamNumber)
                    .Take(9)
                    .Select(x => (x.Id, x.AnimJamNumber, x.WorkUrl, x.MessageUrl, x.DisplayName, x.AttachmentCount))
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
                    DisplayName = w.DisplayName,
                    AttachmentsCount = w.AttachmentCount
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
        [HttpGet]
        public async Task<IActionResult> TodayEventsJson(
    [FromServices] AppDbContext db,
    [FromServices] IMemoryCache cache)
        {
            const string cacheKey = "today_events_widget";
            if (cache.TryGetValue(cacheKey, out List<object>? cached) && cached != null)
                return Json(cached);

            var now = DateTime.UtcNow;
            var rangeStart = now.Date.AddDays(-2);
            var rangeEnd = now.Date.AddDays(3);

            var events = await db.CalendarEvents
                .Where(e => !e.IsHidden && e.StartUtc < rangeEnd && e.EndUtc > rangeStart)
                .ToListAsync();

            var presetIds = events.Where(e => e.EventPresetId.HasValue)
                .Select(e => e.EventPresetId!.Value).Distinct().ToList();
            var presetColors = await db.EventPresets
                .Where(p => presetIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Color ?? "primary");

            var result = new List<object>();
            foreach (var ev in events)
            {
                string color = ev.EventPresetId.HasValue
                    ? presetColors.GetValueOrDefault(ev.EventPresetId.Value, "primary")
                    : "primary";

                if (ev.IsRecurring && !string.IsNullOrEmpty(ev.RecurrenceRuleJson))
                {
                    var occs = GetOccurrencesHelper(ev, rangeStart, rangeEnd);
                    foreach (var occ in occs)
                    {
                        var occEnd = occ + (ev.EndUtc - ev.StartUtc);
                        result.Add(new
                        {
                            id = ev.Id,
                            title = ev.Title,
                            startUtc = occ.ToString("o"),
                            endUtc = occEnd.ToString("o"),
                            color
                        });
                    }
                }
                else
                {
                    result.Add(new
                    {
                        id = ev.Id,
                        title = ev.Title,
                        startUtc = ev.StartUtc.ToString("o"),
                        endUtc = ev.EndUtc.ToString("o"),
                        color
                    });
                }
            }

            cache.Set(cacheKey, result, TimeSpan.FromMinutes(2));
            return Json(result);
        }

        private static List<DateTime> GetOccurrencesHelper(CalendarEvent ev, DateTime rangeStart, DateTime rangeEnd)
        {
            var result = new List<DateTime>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(ev.RecurrenceRuleJson!);
                var root = doc.RootElement;
                int frequency = root.TryGetProperty("frequency", out var f) ? f.GetInt32() : 2;
                int interval = root.TryGetProperty("interval", out var i) && i.GetInt32() > 0 ? i.GetInt32() : 1;
                int[]? byWeekday = null;
                if (root.TryGetProperty("by_weekday", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Array)
                    byWeekday = w.EnumerateArray().Select(x => x.GetInt32()).ToArray();

                var exceptions = new HashSet<DateTime>();
                if (!string.IsNullOrEmpty(ev.RecurrenceExceptionsJson))
                {
                    try
                    {
                        var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(ev.RecurrenceExceptionsJson);
                        if (list != null)
                            foreach (var s in list)
                                if (DateTime.TryParse(s, null,
                                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                                    out var dt))
                                    exceptions.Add(dt);
                    }
                    catch { }
                }

                var start = ev.StartUtc;

                if (frequency == 2)
                {
                    var byWeekdayList = byWeekday ?? new[] { ((int)start.DayOfWeek + 6) % 7 };
                    var startMonday = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                    var rangeMonday = rangeStart.Date.AddDays(-(((int)rangeStart.DayOfWeek + 6) % 7));

                    var iter = startMonday;
                    while (iter < rangeMonday) iter = iter.AddDays(7 * interval);
                    if (iter > rangeMonday) iter = iter.AddDays(-7 * interval);

                    while (iter < rangeEnd)
                    {
                        foreach (var wd in byWeekdayList)
                        {
                            var date = iter.AddDays(wd);
                            var occ = new DateTime(date.Year, date.Month, date.Day,
                                start.Hour, start.Minute, start.Second, DateTimeKind.Utc);
                            if (occ < start) continue;
                            if (occ < rangeStart || occ >= rangeEnd) continue;
                            if (exceptions.Contains(occ)) continue;
                            result.Add(occ);
                        }
                        iter = iter.AddDays(7 * interval);
                    }
                }
                else if (frequency == 3)
                {
                    var iter = start;
                    while (iter < rangeStart) iter = iter.AddDays(interval);
                    while (iter < rangeEnd)
                    {
                        if (!exceptions.Contains(iter)) result.Add(iter);
                        iter = iter.AddDays(interval);
                    }
                }
                else
                {
                    if (start >= rangeStart && start < rangeEnd) result.Add(start);
                }
            }
            catch { }
            return result;
        }

    }
}