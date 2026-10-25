using DSharpPlus;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace AnimatorsWeb.Services
{
    public class FeaturedThumbnailService
    {
        private readonly IWebHostEnvironment _env;
        private readonly IHttpClientFactory _httpFactory;
        private readonly DiscordClient _client;
        private readonly IConfiguration _config;
        private readonly ILogger<FeaturedThumbnailService> _logger;
        private static readonly SemaphoreSlim _concurrencyLimit = new(1, 1);
        public string GetFilePath(int participantId) => FilePath(participantId);

        private const int Width = 640;
        private const int Height = 400;
        private const int JpegQuality = 85;

        private static readonly ConcurrentDictionary<int, byte> _inProgress = new();
        private static bool? _ffmpegAvailable;

        public FeaturedThumbnailService(
            IWebHostEnvironment env,
            IHttpClientFactory httpFactory,
            DiscordClient client,
            IConfiguration config,
            ILogger<FeaturedThumbnailService> logger)
        {
            _env = env;
            _httpFactory = httpFactory;
            _client = client;
            _config = config;
            _logger = logger;
        }

        private string FolderPath => Path.Combine(_env.WebRootPath, "featured");
        private string FilePath(int id) => Path.Combine(FolderPath, $"{id}.jpg");

        public string? GetUrl(int id)
        {
            var path = FilePath(id);
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path).Ticks;
            return $"/featured/{id}.jpg?v={stamp}";
        }

        public bool HasThumbnail(int id) => File.Exists(FilePath(id));

        public void Remove(int id)
        {
            try { File.Delete(FilePath(id)); } catch { }
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
        /// Генерирует обложку, если её ещё нет. Возвращает true, если файл существует в итоге.
        /// Безопасен для параллельных вызовов: для одного participantId генерация идёт один раз.
        /// </summary>
        public async Task<bool> EnsureAsync(int participantId, string workUrl, string messageUrl, string mediaType)
        {
            if (File.Exists(FilePath(participantId))) return true;
            if (!_inProgress.TryAdd(participantId, 0)) return false;

            await _concurrencyLimit.WaitAsync();
            try { return await GenerateAsync(participantId, workUrl, messageUrl, mediaType); }
            finally
            {
                _concurrencyLimit.Release();
                _inProgress.TryRemove(participantId, out _);
            }
        }

        private async Task<bool> GenerateAsync(int participantId, string workUrl, string messageUrl, string mediaType)
        {
            try
            {
                Directory.CreateDirectory(FolderPath);

                // YouTube — ImageSharp (mqdefault.jpg маленький, 320x180, безопасно)
                var ytId = ExtractYouTubeId(workUrl);
                if (ytId != null)
                {
                    return await DownloadAndResizeAsync(
                        $"https://img.youtube.com/vi/{ytId}/mqdefault.jpg", participantId);
                }

                // Всё остальное (video, gif, png, jpg) — через ffmpeg
                if (mediaType == "video" || mediaType == "image")
                {
                    var freshUrl = await TryGetFreshAttachmentAsync(messageUrl);
                    if (string.IsNullOrEmpty(freshUrl)) return false;

                    return await ExtractFrameViaFfmpegAsync(freshUrl, participantId);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Thumbnail generation failed for participant {Id}", participantId);
                return false;
            }
        }
        private async Task<bool> ExtractFrameViaFfmpegAsync(string url, int participantId)
        {
            if (!IsFfmpegAvailable()) return false;

            var ffmpeg = _config["Features:FfmpegPath"] ?? "ffmpeg";
            var ext = GetExtensionFromUrl(url);
            var tmpIn = Path.Combine(Path.GetTempPath(), $"thumb_{participantId}_{Guid.NewGuid():N}{ext}");
            var tmpOut = Path.Combine(Path.GetTempPath(), $"thumb_{participantId}_{Guid.NewGuid():N}.jpg");

            try
            {
                // 1. Скачиваем на диск потоково — не в память
                try
                {
                    var http = _httpFactory.CreateClient();
                    http.Timeout = TimeSpan.FromMinutes(2);

                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Download failed {Code} for {Id}", resp.StatusCode, participantId);
                        return false;
                    }

                    var len = resp.Content.Headers.ContentLength ?? 0;
                    if (len > 500_000_000)  
                    {
                        _logger.LogWarning("File too large ({MB} MB) for {Id}", len / 1_000_000, participantId);
                        return false;
                    }

                    await using var fs = System.IO.File.Create(tmpIn);
                    await resp.Content.CopyToAsync(fs);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Download error for {Id}", participantId);
                    return false;
                }

                // 2. ffmpeg извлекает первый кадр (потоково, ~50-100 МБ RAM)
                var vf = $"scale={Width}:{Height}:force_original_aspect_ratio=increase,crop={Width}:{Height}";
                var args = $"-y -ss 0 -i \"{tmpIn}\" -vf \"{vf}\" -frames:v 1 -threads 1 -an -q:v 3 \"{tmpOut}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return false;

                var stderrTask = process.StandardError.ReadToEndAsync();
                var timeout = Task.Delay(TimeSpan.FromSeconds(60));
                var completed = await Task.WhenAny(process.WaitForExitAsync(), timeout);

                if (completed == timeout)
                {
                    try { process.Kill(true); } catch { }
                    _logger.LogWarning("ffmpeg timeout for {Id}", participantId);
                    return false;
                }

                await stderrTask;

                if (process.ExitCode != 0 || !System.IO.File.Exists(tmpOut))
                {
                    _logger.LogWarning("ffmpeg exit {Code} for {Id}", process.ExitCode, participantId);
                    return false;
                }

                var finalPath = FilePath(participantId);
                if (System.IO.File.Exists(finalPath)) System.IO.File.Delete(finalPath);
                System.IO.File.Move(tmpOut, finalPath);
                return true;
            }
            finally
            {
                try { if (System.IO.File.Exists(tmpIn)) System.IO.File.Delete(tmpIn); } catch { }
                try { if (System.IO.File.Exists(tmpOut)) System.IO.File.Delete(tmpOut); } catch { }
            }
        }
        private static string GetExtensionFromUrl(string url)
        {
            try
            {
                var path = new Uri(url).AbsolutePath;
                var ext = Path.GetExtension(path);
                if (!string.IsNullOrEmpty(ext) && ext.Length <= 5) return ext.ToLowerInvariant();
            }
            catch { }
            return ".bin";
        }

        private bool IsFfmpegAvailable()
        {
            if (_ffmpegAvailable.HasValue) return _ffmpegAvailable.Value;

            var path = _config["Features:FfmpegPath"] ?? "ffmpeg";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null)
                {
                    _ffmpegAvailable = false;
                    return false;
                }
                p.WaitForExit(3000);
                _ffmpegAvailable = p.ExitCode == 0;

                if (!_ffmpegAvailable.Value)
                    _logger.LogWarning("ffmpeg returned non-zero exit code");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ffmpeg not found at '{Path}' — video thumbnails will be skipped", path);
                _ffmpegAvailable = false;   // ← ключевая строка
            }

            return _ffmpegAvailable.Value;
        }

        

        private async Task<bool> DownloadAndResizeAsync(string url, int participantId)
        {
            try
            {
                var http = _httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(30);

                // ↓ ДОБАВИТЬ: сначала узнаём размер, потом решаем
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) return false;

                var len = resp.Content.Headers.ContentLength ?? 0;
                if (len > 30_000_000)  // 30 МБ — большие гифки пропускаем
                {
                    _logger.LogWarning("File too large ({MB} MB) for {Id}", len / 1_000_000, participantId);
                    return false;
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync();
                using var image = Image.Load<Rgba32>(bytes);

                if (image.Frames.Count > 1)
                {
                    using var firstFrame = image.Frames.CloneFrame(0);
                    SaveResizedToFile(firstFrame, participantId);
                }
                else
                {
                    SaveResizedToFile(image, participantId);
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Image download failed for participant {Id}", participantId);
                return false;
            }
        }

        private void SaveResizedToFile(Image<Rgba32> image, int participantId)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(Width, Height),
                Mode = ResizeMode.Crop,
                Position = AnchorPositionMode.Center
            }));

            using var flat = new Image<Rgb24>(Width, Height, new Rgb24(0, 0, 0));
            flat.Mutate(x => x.DrawImage(image, 1f));

            var finalPath = FilePath(participantId);
            var tmpPath = finalPath + ".tmp";
            flat.SaveAsJpeg(tmpPath, new JpegEncoder { Quality = JpegQuality });

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(tmpPath, finalPath);
        }

        private async Task<string?> TryGetDiscordThumbnailAsync(string messageUrl)
        {
            var (chId, msgId) = DiscordLinkParser.ParseMessageUrl(messageUrl);
            if (chId == null || msgId == null) return null;

            try
            {
                var channel = await _client.GetChannelAsync(chId.Value);
                await Task.Delay(250);
                if (channel == null) return null;
                var msg = await channel.GetMessageAsync(msgId.Value);
                await Task.Delay(250);  
                if (msg == null) return null;

                var thumb = msg.Embeds?.FirstOrDefault()?.Thumbnail?.Url;
                if (thumb == null) return null;
                var s = thumb.ToString();
                return string.IsNullOrEmpty(s) ? null : s;
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
                if (msg == null) return null;

                // 1. Attachment
                var att = msg.Attachments?.FirstOrDefault()?.Url;
                if (att != null)
                {
                    var s = att.ToString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }

                // 2. Embed image
                var embed = msg.Embeds?.FirstOrDefault();
                if (embed != null)
                {
                    var img = embed.Image?.Url;
                    if (img != null)
                    {
                        var s = img.ToString();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }

                    var thumb = embed.Thumbnail?.Url;
                    if (thumb != null)
                    {
                        var s = thumb.ToString();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }

                return null;
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