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

        public RulesService(AppDbContext db, DiscordMessageService discord)
        {
            _db = db;
            _discord = discord;
        }

        public async Task<RulesSettings> GetAsync()
        {
            var settings = await _db.RulesSettings.FirstOrDefaultAsync();
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

            var nonEmpty = sections
                .Where(s => !string.IsNullOrWhiteSpace(s.Content))
                .Select(s => s.Content.Trim())
                .ToList();

            settings.SectionsJson = JsonSerializer.Serialize(sections);
            settings.Content = string.Join("\n\n", nonEmpty); // для обратной совместимости

            var existingIds = ParseMessageIds(settings.AllMessageIds);

            if (settings.ChannelId != 0 && nonEmpty.Count > 0)
            {
                var chunks = BuildChunks(sections, RulesConstants.MaxMessageLength);

                var ids = await _discord.PublishRulesChunksAsync(settings.ChannelId, chunks, existingIds);

                settings.AllMessageIds = string.Join(",", ids);
                settings.LastMessageId = ids.LastOrDefault();

                if (settings.LastMessageId != 0)
                    await _discord.AddAcceptReactionAsync(settings.ChannelId, settings.LastMessageId);
            }

            if (settings.Id == 0)
                _db.RulesSettings.Add(settings);
            else
                _db.RulesSettings.Update(settings);

            if (settings.GuildId == 0)
                settings.GuildId = _discord.GetFirstGuildId();

            await _db.SaveChangesAsync();
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