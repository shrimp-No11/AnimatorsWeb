using DSharpPlus;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AnimatorsWeb.Services
{
    public class FeaturedThumbnailService
    {
        private readonly IWebHostEnvironment _env;
        private readonly IHttpClientFactory _httpFactory;
        private readonly DiscordClient _client;

        private const int Width = 640;
        private const int Height = 400;
        private const int JpegQuality = 85;

        public FeaturedThumbnailService(
            IWebHostEnvironment env,
            IHttpClientFactory httpFactory,
            DiscordClient client)
        {
            _env = env;
            _httpFactory = httpFactory;
            _client = client;
        }

        private string FolderPath => Path.Combine(_env.WebRootPath, "featured");
        private string FilePath(int participantId) => Path.Combine(FolderPath, $"{participantId}.jpg");
        private string WebPath(int participantId) => $"/featured/{participantId}.jpg";

        public string? GetUrl(int participantId)
        {
            var path = FilePath(participantId);
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path).Ticks;
            return $"{WebPath(participantId)}?v={stamp}";
        }

        public void Remove(int participantId)
        {
            try { File.Delete(FilePath(participantId)); } catch { }
        }

        public void ClearAll()
        {
            try
            {
                if (!Directory.Exists(FolderPath)) return;
                foreach (var f in Directory.GetFiles(FolderPath, "*.jpg"))
                    File.Delete(f);
            }
            catch { }
        }

        /// <summary>
        /// Скачивает/генерирует обложку и сохраняет в wwwroot/featured/{id}.jpg.
        /// Возвращает true, если файл появился.
        /// </summary>
        public async Task<bool> EnsureAsync(
            int participantId, string workUrl, string messageUrl, string mediaType)
        {
            try
            {
                Directory.CreateDirectory(FolderPath);

                var sourceUrl = await ResolveSourceUrlAsync(workUrl, messageUrl, mediaType);
                if (string.IsNullOrEmpty(sourceUrl)) return false;

                byte[] bytes;
                try
                {
                    var http = _httpFactory.CreateClient();
                    http.Timeout = TimeSpan.FromSeconds(30);
                    bytes = await http.GetByteArrayAsync(sourceUrl);
                }
                catch { return false; }

                using var image = Image.Load<Rgba32>(bytes);

                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(Width, Height),
                    Mode = ResizeMode.Crop,
                    Position = AnchorPositionMode.Center
                }));

                // Композиция на чёрный (для прозрачности)
                using var flat = new Image<Rgb24>(Width, Height, new Rgb24(0, 0, 0));
                flat.Mutate(x => x.DrawImage(image, 1f));

                var finalPath = FilePath(participantId);
                var tmpPath = finalPath + ".tmp";

                await flat.SaveAsJpegAsync(tmpPath, new JpegEncoder { Quality = JpegQuality });

                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(tmpPath, finalPath);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task<string?> ResolveSourceUrlAsync(
            string workUrl, string messageUrl, string mediaType)
        {
            // YouTube — стабильный CDN
            var ytId = ExtractYouTubeId(workUrl);
            if (ytId != null)
                return $"https://img.youtube.com/vi/{ytId}/mqdefault.jpg";

            // mp4/webm/mov — пробуем Discord thumbnail из embed
            if (mediaType == "video")
            {
                var thumb = await TryGetDiscordThumbnailAsync(messageUrl);
                return thumb; // может быть null
            }

            // gif/png/jpg — нужен свежий URL из Discord (CDN протухает)
            if (mediaType == "image")
                return await TryGetFreshAttachmentAsync(messageUrl);

            return null;
        }

        private async Task<string?> TryGetDiscordThumbnailAsync(string messageUrl)
        {
            var (chId, msgId) = DiscordLinkParser.ParseMessageUrl(messageUrl);
            if (chId == null || msgId == null) return null;

            try
            {
                var channel = await _client.GetChannelAsync(chId.Value);
                if (channel == null) return null;
                var msg = await channel.GetMessageAsync(msgId.Value);
                if (msg == null) return null;

                var thumb = msg.Embeds?.FirstOrDefault()?.Thumbnail?.Url;
                if (thumb == null) return null;

                var thumbStr = thumb.ToString();
                return string.IsNullOrEmpty(thumbStr) ? null : thumbStr;
            }
            catch { return null; }
        }

        private async Task<string?> TryGetFreshAttachmentAsync(string messageUrl)
        {
            var (chId, msgId) = DiscordLinkParser.ParseMessageUrl(messageUrl);
            if (chId == null || msgId == null) return null;

            try
            {
                var channel = await _client.GetChannelAsync(chId.Value);
                if (channel == null) return null;
                var msg = await channel.GetMessageAsync(msgId.Value);
                return msg?.Attachments?.FirstOrDefault()?.Url;
            }
            catch { return null; }
        }

        private static string? ExtractYouTubeId(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(url,
                @"(?:youtu\.be/|youtube\.com/(?:watch\?v=|embed/|v/))([a-zA-Z0-9_-]{11})");
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}