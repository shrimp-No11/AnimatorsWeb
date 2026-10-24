using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using DSharpPlus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace AnimatorsWeb.Services
{
    public class DesignSettingsService
    {
        private readonly AppDbContext _db;
        private readonly PaletteExtractionService _palette;
        private readonly IMemoryCache _cache;
        private readonly DiscordClient _client;
        private readonly IHttpClientFactory _httpFactory;
        private readonly IWebHostEnvironment _env;

        private const string CacheKey = "DesignSettings_Cache";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        private const string AvatarFileName = "guild-avatar.png";
        private const string AvatarWebPath = "/images/guild-avatar.png";

        public DesignSettingsService(
            AppDbContext db,
            PaletteExtractionService palette,
            IMemoryCache cache,
            DiscordClient client,
            IHttpClientFactory httpFactory,
            IWebHostEnvironment env)
        {
            _db = db;
            _palette = palette;
            _cache = cache;
            _client = client;
            _httpFactory = httpFactory;
            _env = env;
        }

        public async Task<DesignSettings> GetAsync()
        {
            if (_cache.TryGetValue(CacheKey, out DesignSettings? cached) && cached != null)
                return cached;

            var settings = await _db.DesignSettings.FirstOrDefaultAsync();
            if (settings == null)
            {
                settings = new DesignSettings();
                _db.DesignSettings.Add(settings);
                await _db.SaveChangesAsync();
            }

            _cache.Set(CacheKey, settings, CacheDuration);
            return settings;
        }

        public async Task SaveAsync(DesignSettings settings)
        {
            _db.DesignSettings.Update(settings);
            await _db.SaveChangesAsync();
            _cache.Remove(CacheKey);
        }

        /// <summary>URL локально сохранённой аватарки (с cache-buster) или null, если файла нет.</summary>
        public string? GetLocalAvatarUrl()
        {
            var diskPath = Path.Combine(_env.WebRootPath, "images", AvatarFileName);
            if (!File.Exists(diskPath)) return null;

            var stamp = File.GetLastWriteTimeUtc(diskPath).Ticks;
            return $"{AvatarWebPath}?v={stamp}";
        }

        /// <summary>URL аватарки прямо с Discord CDN (fallback до первой загрузки).</summary>
        public string? GetGuildIconUrl()
        {
            var guild = _client.Guilds.Values.FirstOrDefault();
            return guild?.IconUrl;
        }

        /// <summary>
        /// Скачивает аватарку, сохраняет в wwwroot/images/guild-avatar.png,
        /// извлекает палитру и сохраняет цвета. Возвращает null при успехе, иначе — текст ошибки.
        /// </summary>
        public async Task<string?> UpdateFromAvatarAsync()
        {
            var guild = _client.Guilds.Values.FirstOrDefault();
            if (guild == null) return "Гильдия не найдена";

            var iconUrl = guild.IconUrl;
            if (string.IsNullOrEmpty(iconUrl)) return "У сервера нет аватарки";

            // Просим у Discord версию побольше, если параметра ещё нет
            var downloadUrl = iconUrl.Contains("size=")
                ? iconUrl
                : iconUrl + (iconUrl.Contains('?') ? "&" : "?") + "size=256";

            // 1. Скачиваем один раз
            byte[] bytes;
            try
            {
                var http = _httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(30);
                bytes = await http.GetByteArrayAsync(downloadUrl);
            }
            catch (Exception ex)
            {
                return $"Не удалось скачать аватарку: {ex.Message}";
            }

            // 2. Сохраняем файл (атомарно: tmp + move)
            try
            {
                var dir = Path.Combine(_env.WebRootPath, "images");
                Directory.CreateDirectory(dir);

                var finalPath = Path.Combine(dir, AvatarFileName);
                var tmpPath = finalPath + ".tmp";

                await File.WriteAllBytesAsync(tmpPath, bytes);
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(tmpPath, finalPath);
            }
            catch (Exception ex)
            {
                return $"Не удалось сохранить аватарку: {ex.Message}";
            }

            // после сохранения guild-avatar.png

            // 2.1. Favicon 32x32
            try
            {
                using var image = Image.Load<Rgba32>(bytes);
                using var resized = image.Clone(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(32, 32),
                    Mode = ResizeMode.Crop,
                    Position = AnchorPositionMode.Center
                }));
                var favPath = Path.Combine(_env.WebRootPath, "favicon-32.png");
                await resized.SaveAsPngAsync(favPath);
            }
            catch (Exception ex)
            {
                return $"Не удалось сохранить favicon: {ex.Message}";
            }

            // 2.2. Apple touch icon 180x180
            try
            {
                using var image = Image.Load<Rgba32>(bytes);
                using var resized = image.Clone(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(180, 180),
                    Mode = ResizeMode.Crop,
                    Position = AnchorPositionMode.Center
                }));
                var touchPath = Path.Combine(_env.WebRootPath, "apple-touch-icon.png");
                await resized.SaveAsPngAsync(touchPath);
            }
            catch (Exception ex)
            {
                return $"Не удалось сохранить apple-touch-icon: {ex.Message}";
            }

            // 3. Извлекаем палитру из тех же байтов
            DesignPalette palette;
            try
            {
                palette = _palette.ExtractFromBytes(bytes);
            }
            catch (Exception ex)
            {
                return $"Не удалось извлечь палитру: {ex.Message}";
            }

            // 4. Сохраняем цвета
            var settings = await _db.DesignSettings.FirstOrDefaultAsync();
            if (settings == null)
            {
                settings = new DesignSettings();
                _db.DesignSettings.Add(settings);
            }

            settings.BackgroundColor = palette.Background;
            settings.TextColor = palette.Text;
            settings.PrimaryColor = palette.Primary;
            settings.PrimaryHoverColor = palette.PrimaryHover;
            settings.AccentColor = palette.Accent;
            settings.CardColor = palette.Card;
            settings.PaletteUpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            _cache.Remove(CacheKey);

            return null;
        }
    }
}