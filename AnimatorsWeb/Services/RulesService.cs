using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using Microsoft.EntityFrameworkCore;

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

        public async Task SaveAsync(RulesSettings settings)
        {
            // Публикация/редактирование сообщений в Discord
            var existingIds = ParseMessageIds(settings.AllMessageIds);

            if (settings.ChannelId != 0 && !string.IsNullOrWhiteSpace(settings.Content))
            {
                var ids = await _discord.PublishRulesAsync(settings.ChannelId, settings.Content, existingIds);

                settings.AllMessageIds = string.Join(",", ids);
                settings.LastMessageId = ids.LastOrDefault();

                // Ставим реакцию ✅ под последним сообщением
                if (settings.LastMessageId != 0)
                    await _discord.AddAcceptReactionAsync(settings.ChannelId, settings.LastMessageId);
            }

            if (settings.Id == 0)
                _db.RulesSettings.Add(settings);
            else
                _db.RulesSettings.Update(settings);

            if (settings.GuildId == 0)
            {
                var guild = _discord.GetFirstGuildId();
                settings.GuildId = guild;
            }

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
    }
}