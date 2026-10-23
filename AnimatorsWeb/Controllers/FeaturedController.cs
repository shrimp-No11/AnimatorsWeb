using AnimatorsWeb.Data;
using AnimatorsWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Controllers
{
    [AllowAnonymous]
    public class FeaturedController : Controller
    {
        private readonly AppDbContext _db;
        private readonly FeaturedThumbnailService _thumb;

        public FeaturedController(AppDbContext db, FeaturedThumbnailService thumb)
        {
            _db = db;
            _thumb = thumb;
        }

        [ResponseCache(Duration = 604800, Location = ResponseCacheLocation.Any)]
        public async Task<IActionResult> Thumbnail(int id)
        {
            var path = _thumb.GetFilePath(id);

            // Файл уже есть — отдаём сразу
            if (System.IO.File.Exists(path))
                return PhysicalFile(path, "image/jpeg");

            // Нет — пробуем сгенерировать на месте
            var participant = await _db.Participants
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id);

            if (participant == null) return NotFound();

            var mediaType = DetectMediaType(participant.WorkUrl);
            if (mediaType != "video" && mediaType != "image" && mediaType != "youtube")
                return NotFound();

            await _thumb.EnsureAsync(id, participant.WorkUrl, participant.MessageUrl, mediaType);

            if (System.IO.File.Exists(path))
                return PhysicalFile(path, "image/jpeg");

            return NotFound();
        }

        private static string DetectMediaType(string workUrl)
        {
            if (string.IsNullOrEmpty(workUrl)) return "link";
            var lower = workUrl.ToLowerInvariant();

            if (lower.Contains("youtube.com") || lower.Contains("youtu.be")) return "youtube";
            if (lower.Contains(".mp4") || lower.Contains(".webm") || lower.Contains(".mov")) return "video";
            if (System.Text.RegularExpressions.Regex.IsMatch(workUrl,
                @"\.(gif|png|jpg|jpeg)(\?|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return "image";
            return "link";
        }
    }
}