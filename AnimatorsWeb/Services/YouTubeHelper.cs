using System.Text.RegularExpressions;

namespace AnimatorsWeb.Services
{
    public static class YouTubeHelper
    {
        // Единый regex: watch, embed, shorts, live, v, youtu.be
        private static readonly Regex _idRegex = new Regex(
            @"(?:youtu\.be/|youtube\.com/(?:watch\?v=|embed/|shorts/|live/|v/))([a-zA-Z0-9_-]{11})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Возвращает ID видео из любой ссылки YouTube, либо null.</summary>
        public static string? ExtractId(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var m = _idRegex.Match(url);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>Проверяет, является ли ссылка YouTube.</summary>
        public static bool IsYouTubeUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            var lower = url.ToLowerInvariant();
            return lower.Contains("youtube.com") || lower.Contains("youtu.be");
        }

        /// <summary>Готовый URL обложки mqdefault (320×180).</summary>
        public static string? GetThumbnailUrl(string? url)
        {
            var id = ExtractId(url);
            return id == null ? null : $"https://img.youtube.com/vi/{id}/mqdefault.jpg";
        }
    }
}