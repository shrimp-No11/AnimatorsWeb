using AnimatorsWeb.Data;
using AnimatorsWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AnimatorsWeb.Controllers
{
    [AllowAnonymous]
    public class MediaController : Controller
    {
        private readonly AppDbContext _db;
        private readonly DiscordMessageService _discord;

        private static readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 200_000_000 // 200 МБ
        });

        public MediaController(AppDbContext db, DiscordMessageService discord)
        {
            _db = db;
            _discord = discord;
        }

        public async Task<IActionResult> StreamWork(int id)
        {
            var p = await _db.Participants.FindAsync(id);
            if (p == null) return NotFound();

            var cacheKey = $"work_{id}";

            // === Из кэша ===
            if (_cache.TryGetValue(cacheKey, out byte[]? cachedBytes) && cachedBytes != null)
            {
                var cachedType = GuessContentType(p.WorkUrl);
                return File(cachedBytes, cachedType, enableRangeProcessing: true);
            }

            // === Запрашиваем у Discord ===
            var (channelId, messageId) = DiscordLinkParser.ParseMessageUrl(p.MessageUrl);

            if (channelId == null || messageId == null)
            {
                if (!string.IsNullOrEmpty(p.MessageUrl))
                    return Redirect(p.MessageUrl);
                return NotFound();
            }

            string? freshUrl;
            try
            {
                freshUrl = await _discord.GetFreshAttachmentUrlAsync(channelId.Value, messageId.Value);
            }
            catch
            {
                freshUrl = null;
            }

            if (string.IsNullOrEmpty(freshUrl))
            {
                if (!string.IsNullOrEmpty(p.MessageUrl))
                    return Redirect(p.MessageUrl);
                return NotFound();
            }

            byte[] bytes;
            string contentType;

            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromMinutes(5);

                using var response = await http.GetAsync(freshUrl, HttpCompletionOption.ResponseContentRead);
                if (!response.IsSuccessStatusCode)
                {
                    if (!string.IsNullOrEmpty(p.MessageUrl))
                        return Redirect(p.MessageUrl);
                    return NotFound();
                }

                bytes = await response.Content.ReadAsByteArrayAsync();
                contentType = response.Content.Headers.ContentType?.MediaType
                              ?? GuessContentType(p.WorkUrl);
            }
            catch
            {
                if (!string.IsNullOrEmpty(p.MessageUrl))
                    return Redirect(p.MessageUrl);
                return NotFound();
            }

            // === Сохраняем в кэш ===
            _cache.Set(cacheKey, bytes, new MemoryCacheEntryOptions
            {
                Size = bytes.Length,
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6)
            });

            return File(bytes, contentType, enableRangeProcessing: true);
        }

        private static string GuessContentType(string url)
        {
            if (string.IsNullOrEmpty(url)) return "application/octet-stream";
            var lower = url.ToLowerInvariant();

            if (lower.Contains(".mp4")) return "video/mp4";
            if (lower.Contains(".webm")) return "video/webm";
            if (lower.Contains(".mov")) return "video/quicktime";
            if (lower.Contains(".gif")) return "image/gif";
            if (lower.Contains(".png")) return "image/png";
            if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return "image/jpeg";

            return "application/octet-stream";
        }
    }
}