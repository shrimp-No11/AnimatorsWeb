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

        private sealed class CachedMedia
        {
            public byte[] Bytes { get; init; } = Array.Empty<byte>();
            public string ContentType { get; init; } = "application/octet-stream";
        }
        private static readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 100_000_000 // 100 МБ
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

            var cacheKey = $"work_{id}_p{p.PrimaryAttachmentIndex}";

            // === Из кэша ===
            if (_cache.TryGetValue(cacheKey, out CachedMedia? cachedMedia) && cachedMedia != null)
                return File(cachedMedia.Bytes, cachedMedia.ContentType, enableRangeProcessing: true);

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
                var attachments = await _discord.GetFreshAttachmentsAsync(p.MessageUrl);
                var idx = p.PrimaryAttachmentIndex;
                if (attachments.Count > 0)
                {
                    var chosen = idx >= 0 && idx < attachments.Count ? attachments[idx] : attachments[0];
                    freshUrl = chosen.Url;
                }
                else
                {
                    freshUrl = null;
                }
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
                contentType = GuessContentTypeFromBytes(bytes)
                              ?? response.Content.Headers.ContentType?.MediaType
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
            if (lower.Contains(".m4v")) return "video/x-m4v";
            if (lower.Contains(".gif")) return "image/gif";
            if (lower.Contains(".png")) return "image/png";
            if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return "image/jpeg";
            if (lower.Contains(".webp")) return "image/webp";
            if (lower.Contains(".avif")) return "image/avif";
            if (lower.Contains(".bmp")) return "image/bmp";

            return "application/octet-stream";
        }

        /// <summary>
        /// Определяет Content-Type по первым байтам файла (magic numbers).
        /// Надёжнее, чем по расширению: работает, даже если URL без расширения.
        /// </summary>
        private static string? GuessContentTypeFromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12) return null;

            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return "image/png";

            // JPEG: FF D8 FF
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return "image/jpeg";

            // GIF: 47 49 46 38 ("GIF8")
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
                return "image/gif";

            // WEBP: RIFF....WEBP
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                return "image/webp";

            // AVIF / MP4 / MOV — все начинаются с ftyp; различия в brand на байтах 8–11
            if (bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
            {
                var brand = System.Text.Encoding.ASCII.GetString(bytes, 8, 4);
                if (brand == "avif" || brand == "avis") return "image/avif";
                if (brand == "qt  ") return "video/quicktime";
                return "video/mp4";
            }

            // WebM / MKV: 1A 45 DF A3
            if (bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3)
                return "video/webm";

            return null;
        }

        [HttpGet("Media/StreamAttachment/{id}/{index}")]
        public async Task<IActionResult> StreamAttachment(int id, int index)
        {
            var p = await _db.Participants.FindAsync(id);
            if (p == null) return NotFound();

            var cacheKey = $"attach_{id}_{index}";

            if (_cache.TryGetValue(cacheKey, out CachedMedia? cached) && cached != null)
                return File(cached.Bytes, cached.ContentType, enableRangeProcessing: true);

            var attachments = await _discord.GetFreshAttachmentsAsync(p.MessageUrl);
            if (index < 0 || index >= attachments.Count) return NotFound();

            var url = attachments[index].Url;
            if (string.IsNullOrEmpty(url)) return NotFound();

            byte[] bytes;
            string contentType;

            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromMinutes(5);
                using var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return NotFound();

                bytes = await resp.Content.ReadAsByteArrayAsync();
                contentType = resp.Content.Headers.ContentType?.MediaType
                              ?? GuessContentType(attachments[index].FileName);
            }
            catch
            {
                return NotFound();
            }

            _cache.Set(cacheKey, new CachedMedia { Bytes = bytes, ContentType = contentType },
            new MemoryCacheEntryOptions
            {
                Size = bytes.Length,
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6)
            });

            return File(bytes, contentType, enableRangeProcessing: true);
        }
        [AllowAnonymous]
        public async Task<IActionResult> AttachmentInfo(int id)
        {
            var p = await _db.Participants.FindAsync(id);
            if (p == null) return NotFound();

            var (chId, msgId) = DiscordLinkParser.ParseMessageUrl(p.MessageUrl);
            if (chId == null || msgId == null)
                return Json(new { count = 0, primaryIndex = 0, items = Array.Empty<object>() });

            List<(string FileName, string Url)> attachments;
            try
            {
                attachments = await _discord.GetFreshAttachmentsAsync(p.MessageUrl);
            }
            catch
            {
                return Json(new { count = 0, primaryIndex = 0, items = Array.Empty<object>() });
            }

            var items = attachments.Select((a, i) =>
            {
                var lower = (a.FileName ?? "").ToLowerInvariant();
                var isVideo = lower.EndsWith(".mp4") || lower.EndsWith(".webm")
                           || lower.EndsWith(".mov") || lower.EndsWith(".m4v");
                var isImage = !isVideo;

                return new
                {
                    index = i,
                    fileName = a.FileName,
                    isVideo,
                    isImage
                };
            }).ToList();

            return Json(new
            {
                count = attachments.Count,
                primaryIndex = p.PrimaryAttachmentIndex,
                items
            });
        }
        [AllowAnonymous]
        public async Task<IActionResult> AttachmentInfoByUrl(string messageUrl)
        {
            if (string.IsNullOrEmpty(messageUrl) || !messageUrl.Contains("discord.com/channels/"))
                return Json(new { count = 0, items = Array.Empty<object>() });

            var (chId, msgId) = DiscordLinkParser.ParseMessageUrl(messageUrl);
            if (chId == null || msgId == null)
                return Json(new { count = 0, items = Array.Empty<object>() });

            List<(string FileName, string Url)> attachments;
            try
            {
                attachments = await _discord.GetFreshAttachmentsAsync(messageUrl);
            }
            catch
            {
                return Json(new { count = 0, items = Array.Empty<object>() });
            }

            var items = attachments.Select((a, i) =>
            {
                var lower = (a.FileName ?? "").ToLowerInvariant();
                var isVideo = lower.EndsWith(".mp4") || lower.EndsWith(".webm")
                           || lower.EndsWith(".mov") || lower.EndsWith(".m4v");
                return new
                {
                    index = i,
                    fileName = a.FileName,
                    isVideo,
                    isImage = !isVideo
                };
            }).ToList();

            return Json(new { count = attachments.Count, items });
        }
        [AllowAnonymous]
        public async Task<IActionResult> PreviewAttachmentByUrl(string messageUrl, int index = 0)
        {
            if (string.IsNullOrEmpty(messageUrl) || !messageUrl.Contains("discord.com/channels/"))
                return NotFound();

            var (chId, msgId) = DiscordLinkParser.ParseMessageUrl(messageUrl);
            if (chId == null || msgId == null) return NotFound();

            List<(string FileName, string Url)> attachments;
            try
            {
                attachments = await _discord.GetFreshAttachmentsAsync(messageUrl);
            }
            catch
            {
                return NotFound();
            }

            if (attachments.Count == 0 || index < 0 || index >= attachments.Count)
                return NotFound();

            var url = attachments[index].Url;
            if (string.IsNullOrEmpty(url)) return NotFound();

            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(30);
                using var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return NotFound();

                var bytes = await resp.Content.ReadAsByteArrayAsync();
                var contentType = GuessContentTypeFromBytes(bytes)
                  ?? resp.Content.Headers.ContentType?.MediaType
                  ?? GuessContentType(attachments[index].FileName);

                Response.Headers["Cache-Control"] = "public, max-age=3600";
                return File(bytes, contentType);
            }
            catch
            {
                return NotFound();
            }
        }
        [AllowAnonymous]
        public async Task<IActionResult> DebugParticipant(int id)
        {
            var p = await _db.Participants.FindAsync(id);
            if (p == null) return Json(new { found = false, id });

            var attachments = await _discord.GetFreshAttachmentsAsync(p.MessageUrl);

            return Json(new
            {
                found = true,
                id,
                messageUrl = p.MessageUrl,
                workUrl = p.WorkUrl,
                attachmentCount = p.AttachmentCount,
                primaryIndex = p.PrimaryAttachmentIndex,
                liveAttachmentCount = attachments.Count,
                names = attachments.Select(a => a.FileName).ToList()
            });
        }
        [AllowAnonymous]
        [HttpGet("Media/DebugAttachment/{id}/{index}")]
        public async Task<IActionResult> DebugAttachment(int id, int index)
        {
            var p = await _db.Participants.FindAsync(id);
            if (p == null) return Json(new { step = "participant_not_found" });

            List<(string FileName, string Url)> attachments;
            try
            {
                attachments = await _discord.GetFreshAttachmentsAsync(p.MessageUrl);
            }
            catch (Exception ex)
            {
                return Json(new { step = "attachments_exception", error = ex.Message });
            }

            if (index < 0 || index >= attachments.Count)
                return Json(new { step = "index_out_of_range", count = attachments.Count });

            var url = attachments[index].Url;
            if (string.IsNullOrEmpty(url))
                return Json(new { step = "url_empty" });

            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromMinutes(2);
                using var resp = await http.GetAsync(url);
                return Json(new
                {
                    step = "http_done",
                    status = (int)resp.StatusCode,
                    contentType = resp.Content.Headers.ContentType?.MediaType,
                    url
                });
            }
            catch (Exception ex)
            {
                return Json(new { step = "http_exception", error = ex.Message, url });
            }
        }
    }
}