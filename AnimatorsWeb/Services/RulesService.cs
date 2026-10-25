using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

namespace AnimatorsWeb.Services
{
    public class RulesService
    {
        private readonly AppDbContext _db;
        private readonly DiscordMessageService _discord;
        private readonly IWebHostEnvironment _env;

        public RulesService(AppDbContext db, DiscordMessageService discord, IWebHostEnvironment env)
        {
            _db = db;
            _discord = discord;
            _env = env;
        }

        public async Task<RulesSettings> GetAsync()
        {
            var settings = await _db.RulesSettings.OrderBy(x => x.Id).FirstOrDefaultAsync();
            return settings ?? new RulesSettings();
        }

        // Перегрузка для совместимости — если где-то вызывается по-старому
        public async Task SaveAsync(RulesSettings settings)
        {
            List<RuleSection> sections;
            if (!string.IsNullOrEmpty(settings.SectionsJson))
            {
                try { sections = JsonSerializer.Deserialize<List<RuleSection>>(settings.SectionsJson) ?? new(); }
                catch { sections = new(); }
            }
            else
            {
                sections = new();
                if (!string.IsNullOrWhiteSpace(settings.Content))
                    sections.Add(new RuleSection { Content = settings.Content });
            }

            await SaveAsync(settings, sections);
        }

        public async Task SaveAsync(RulesSettings settings, List<RuleSection> sections)
        {
            sections ??= new List<RuleSection>();

            // 1. Собираем старые вложенные файлы, чтобы понять, какие удалить
            var oldFiles = new HashSet<string>();
            if (!string.IsNullOrEmpty(settings.SectionsJson))
            {
                try
                {
                    var old = JsonSerializer.Deserialize<List<RuleSection>>(settings.SectionsJson);
                    if (old != null)
                        foreach (var s in old)
                            foreach (var a in s.Attachments)
                                if (!string.IsNullOrEmpty(a.StoredName)) oldFiles.Add(a.StoredName);
                }
                catch { }
            }

            var newFiles = sections
                .SelectMany(s => s.Attachments)
                .Select(a => a.StoredName)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet();

            // 2. Сериализуем и синхронизируем Content
            settings.SectionsJson = JsonSerializer.Serialize(sections);
            settings.Content = string.Join("\n\n", sections
                .Where(s => !string.IsNullOrWhiteSpace(s.Content))
                .Select(s => s.Content.Trim()));

            // 3. Публикация
            var existingIds = ParseMessageIds(settings.AllMessageIds);
            var oldLastId = settings.LastMessageId;   

            if (settings.ChannelId != 0 && sections.Any(s => !string.IsNullOrWhiteSpace(s.Content) || s.Attachments.Count > 0))
            {
                var chunks = BuildChunksWithAttachments(sections, RulesConstants.MaxMessageLength);

                var ids = await _discord.PublishRulesChunksWithFilesAsync(settings.ChannelId, chunks, existingIds);

                settings.AllMessageIds = string.Join(",", ids);
                settings.LastMessageId = ids.LastOrDefault();

                if (settings.LastMessageId != 0)
                {
                    if (oldLastId != 0 && oldLastId != settings.LastMessageId)
                        await _discord.RemoveOwnAcceptReactionAsync(settings.ChannelId, oldLastId);

                    await _discord.AddAcceptReactionAsync(settings.ChannelId, settings.LastMessageId);
                }
            }

            // 4. Сохранение в БД
            if (settings.Id == 0)
                _db.RulesSettings.Add(settings);
            else
                _db.RulesSettings.Update(settings);

            if (settings.GuildId == 0)
                settings.GuildId = _discord.GetFirstGuildId();

            await _db.SaveChangesAsync();

            // 5. Удаляем с диска файлы, которые больше не привязаны ни к одному разделу
            foreach (var stored in oldFiles.Except(newFiles))
            {
                try
                {
                    var path = Path.Combine(_env.WebRootPath, "uploads", "rules", stored);
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                }
                catch { }
            }
        }
        private static List<DiscordMessageService.RulesChunk> BuildChunksWithAttachments(
    List<RuleSection> sections, int maxLen)
        {
            var chunks = new List<DiscordMessageService.RulesChunk>();
            var currentText = new StringBuilder();
            var currentFiles = new List<RuleAttachment>();
            const int sepLen = 2;

            void Flush()
            {
                if (currentText.Length == 0 && currentFiles.Count == 0) return;
                chunks.Add(new DiscordMessageService.RulesChunk
                {
                    Text = currentText.ToString().TrimEnd(),
                    Files = new List<RuleAttachment>(currentFiles)
                });
                currentText.Clear();
                currentFiles.Clear();
            }

            foreach (var section in sections)
            {
                var text = (section.Content ?? "").Trim();
                if (string.IsNullOrEmpty(text) && section.Attachments.Count == 0) continue;

                // Раздел требует начала нового сообщения
                if (section.StartNewMessage && (currentText.Length > 0 || currentFiles.Count > 0))
                    Flush();

                // Слишком длинный раздел — принудительно режем
                if (text.Length > maxLen)
                {
                    Flush();
                    var pieces = SplitLongText(text, maxLen);
                    for (int i = 0; i < pieces.Count; i++)
                    {
                        chunks.Add(new DiscordMessageService.RulesChunk
                        {
                            Text = pieces[i],
                            Files = i == pieces.Count - 1
                                ? new List<RuleAttachment>(section.Attachments)
                                : new List<RuleAttachment>()
                        });
                    }
                    continue;
                }

                // Не влезает по размеру — flush
                int extra = (currentText.Length > 0 ? sepLen : 0) + text.Length;
                if (currentText.Length + extra > maxLen && (currentText.Length > 0 || currentFiles.Count > 0))
                    Flush();

                if (currentText.Length > 0) currentText.Append("\n\n");
                currentText.Append(text);
                currentFiles.AddRange(section.Attachments);
            }

            Flush();
            return chunks;
        }

        private static List<ulong> ParseMessageIds(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<ulong>();
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => ulong.TryParse(s.Trim(), out var id) ? id : 0)
                .Where(id => id != 0)
                .ToList();
        }

        /// <summary>
        /// Склеивает разделы в чанки ≤ maxLen. Раздел считается атомарным:
        /// если он не влезает в текущий чанк — начинается новый чанк с ним.
        /// Если сам раздел больше maxLen — режется принудительно.
        /// </summary>
        private static List<string> BuildChunks(List<RuleSection> sections, int maxLen)
        {
            const int separatorLen = 2; // "\n\n"
            var chunks = new List<string>();
            var buffer = new StringBuilder();

            foreach (var section in sections)
            {
                var text = (section.Content ?? "").Trim();
                if (string.IsNullOrEmpty(text)) continue;

                // Сам раздел больше лимита — режем
                if (text.Length > maxLen)
                {
                    if (buffer.Length > 0)
                    {
                        chunks.Add(buffer.ToString());
                        buffer.Clear();
                    }
                    chunks.AddRange(SplitLongText(text, maxLen));
                    continue;
                }

                var extra = (buffer.Length > 0 ? separatorLen : 0) + text.Length;
                if (buffer.Length + extra > maxLen && buffer.Length > 0)
                {
                    chunks.Add(buffer.ToString());
                    buffer.Clear();
                }

                if (buffer.Length > 0) buffer.Append("\n\n");
                buffer.Append(text);
            }

            if (buffer.Length > 0)
                chunks.Add(buffer.ToString());

            return chunks;
        }

        /// <summary>
        /// Режет один раздел по строкам, потом по словам, потом жёстко.
        /// Используется только когда сам раздел больше лимита Discord.
        /// </summary>
        private static List<string> SplitLongText(string text, int maxLen)
        {
            var result = new List<string>();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var buffer = new StringBuilder();

            void Flush()
            {
                if (buffer.Length == 0) return;
                result.Add(buffer.ToString().TrimEnd());
                buffer.Clear();
            }

            foreach (var line in lines)
            {
                if (line.Length > maxLen)
                {
                    Flush();
                    // Режем длинную строку по словам
                    var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var wbuf = new StringBuilder();
                    foreach (var w in words)
                    {
                        if (w.Length > maxLen)
                        {
                            if (wbuf.Length > 0) { result.Add(wbuf.ToString().TrimEnd()); wbuf.Clear(); }
                            for (int i = 0; i < w.Length; i += maxLen)
                                result.Add(w.Substring(i, Math.Min(maxLen, w.Length - i)));
                            continue;
                        }
                        var candidate = wbuf.Length > 0 ? wbuf.Length + 1 + w.Length : w.Length;
                        if (candidate > maxLen)
                        {
                            result.Add(wbuf.ToString().TrimEnd());
                            wbuf.Clear();
                        }
                        if (wbuf.Length > 0) wbuf.Append(' ');
                        wbuf.Append(w);
                    }
                    if (wbuf.Length > 0) result.Add(wbuf.ToString().TrimEnd());
                    continue;
                }

                var nextLen = buffer.Length > 0 ? buffer.Length + 1 + line.Length : line.Length;
                if (nextLen > maxLen)
                {
                    Flush();
                }
                if (buffer.Length > 0) buffer.Append('\n');
                buffer.Append(line);
            }

            Flush();
            return result;
        }
    }
}