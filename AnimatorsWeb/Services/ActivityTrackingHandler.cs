using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Services
{
    public class ActivityTrackingHandler :
        IEventHandler<MessageCreatedEventArgs>,
        IEventHandler<VoiceStateUpdatedEventArgs>
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ActivityTrackingHandler> _logger;

        public ActivityTrackingHandler(
            IServiceScopeFactory scopeFactory,
            ILogger<ActivityTrackingHandler> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task HandleEventAsync(DiscordClient sender, MessageCreatedEventArgs args)
        {
            if (args.Author == null || args.Author.IsBot) return;
            await TrackAsync(args.Author.Id, ActivityType.Message);
        }

        public async Task HandleEventAsync(DiscordClient sender, VoiceStateUpdatedEventArgs args)
        {
            // Нас интересует только ситуация, когда пользователь находится в голосовом канале.
            // Проверка на переход/подключение не нужна — ON CONFLICT DO NOTHING отсечёт повторы за день.
            if (args.After?.ChannelId == null) return;

            // Получаем гильдию по ID, чтобы проверить, не бот ли это
            if (args.GuildId == null) return;

            DiscordGuild? guild = null;
            try
            {
                guild = await sender.GetGuildAsync(args.GuildId.Value);
            }
            catch
            {
                return; // не удалось получить гильдию — пропускаем
            }

            if (guild == null) return;

            DiscordMember? member = null;
            try
            {
                member = await guild.GetMemberAsync(args.UserId);
            }
            catch
            {
                return; // участник недоступен — пропускаем
            }

            if (member == null || member.IsBot) return;

            await TrackAsync(args.UserId, ActivityType.VoiceJoin);
        }

        private async Task TrackAsync(ulong userId, ActivityType type)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var settings = await db.ActiveRoleSettings.AsNoTracking().FirstOrDefaultAsync();
                if (settings == null || !settings.IsEnabled) return;

                var mskNow = DateTime.UtcNow.AddHours(3);
                var date = DateOnly.FromDateTime(mskNow);

                await db.Database.ExecuteSqlRawAsync(
                    @"INSERT INTO ""UserActivities"" (""DiscordUserId"", ""Date"", ""Type"")
                      VALUES ({0}, {1}, {2})
                      ON CONFLICT DO NOTHING",
                    userId, date, (int)type);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Activity tracking failed for user {UserId} type {Type}", userId, type);
            }
        }
    }
}