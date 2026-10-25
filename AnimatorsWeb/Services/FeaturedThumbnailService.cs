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
                _logger.LogInformation("GenerateAsync START id={Id} mediaType={Type} workUrl={Url}", participantId, mediaType, workUrl);
                Directory.CreateDirectory(FolderPath);

                // Видео — ffmpeg на первом месте
                if (mediaType == "video")
                {
                    var freshUrl = await TryGetFreshAttachmentAsync(messageUrl);
                    if (!string.IsNullOrEmpty(freshUrl) && await TryExtractVideoFrameAsync(freshUrl, participantId))
                        return true;

                    return false;
                }

                // Картинки/GIF — скачиваем свежий URL и ресайзим
                if (mediaType == "image")
                {
                    var freshUrl = await TryGetFreshAttachmentAsync(messageUrl);
                    if (string.IsNullOrEmpty(freshUrl)) return false;
                    return await DownloadAndResizeAsync(freshUrl, participantId);
                }

                // YouTube
                var ytId = ExtractYouTubeId(workUrl);
                if (ytId != null)
                    return await DownloadAndResizeAsync(
                        $"https://img.youtube.com/vi/{ytId}/mqdefault.jpg", participantId);

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Thumbnail generation failed for participant {Id}", participantId);
                return false;
            }
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

        private async Task<bool> TryExtractVideoFrameAsync(string videoUrl, int participantId)
        {
            _logger.LogInformation("TryExtractVideoFrame START participant={Id} url={Url}", participantId, videoUrl);

            if (!IsFfmpegAvailable()) return false;

            var ffmpeg = _config["Features:FfmpegPath"] ?? "ffmpeg";
            var outPath = FilePath(participantId);
            var tmpOut = outPath + ".ffmpeg.jpg";
            var tmpVideo = Path.Combine(Path.GetTempPath(), $"thumb_{participantId}_{Guid.NewGuid():N}.mp4");

            try
            {
                // 1. Скачиваем через HttpClient
                try
                {
                    var http = _httpFactory.CreateClient();
                    http.Timeout = TimeSpan.FromSeconds(60);

                    using var resp = await http.GetAsync(videoUrl, HttpCompletionOption.ResponseHeadersRead);
                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Video download failed {Code} for {Id}", resp.StatusCode, participantId);
                        return false;
                    }

                    var len = resp.Content.Headers.ContentLength ?? 0;
                    if (len > 100_000_000)
                    {
                        _logger.LogWarning("Video too large ({MB} MB) for {Id}", len / 1_000_000, participantId);
                        return false;
                    }

                    await using var fs = File.Create(tmpVideo);
                    await resp.Content.CopyToAsync(fs);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Video download error for {Id}", participantId);
                    return false;
                }

                // 2. ffmpeg по локальному файлу
                var vf = $"scale={Width}:{Height}:force_original_aspect_ratio=increase,crop={Width}:{Height}";
                var args = $"-y -ss 0 -t 2 -i \"{tmpVideo}\" -vf \"{vf}\" -frames:v 1 -threads 1 -q:v 3 \"{tmpOut}\"";
                
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

                if (process.ExitCode != 0 || !File.Exists(tmpOut))
                {
                    _logger.LogWarning("ffmpeg exit {Code} for {Id}", process.ExitCode, participantId);
                    return false;
                }

                if (File.Exists(outPath)) File.Delete(outPath);
                File.Move(tmpOut, outPath);
                return true;
            }
            finally
            {
                try { if (File.Exists(tmpVideo)) File.Delete(tmpVideo); } catch { }
                try { if (File.Exists(tmpOut)) File.Delete(tmpOut); } catch { }
            }
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