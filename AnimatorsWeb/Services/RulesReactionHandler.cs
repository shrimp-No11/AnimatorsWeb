using AnimatorsWeb.Data;
using DSharpPlus;
using DSharpPlus.EventArgs;
using Microsoft.Extensions.DependencyInjection;

namespace AnimatorsWeb.Services
{
    public class RulesReactionHandler : IEventHandler<MessageReactionAddedEventArgs>
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public RulesReactionHandler(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public async Task HandleEventAsync(DiscordClient sender, MessageReactionAddedEventArgs e)
        {
            Console.WriteLine($"Реакция: {e.Emoji.GetDiscordName()} от {e.User.Username} на сообщении {e.Message.Id}");
            if (e.User.IsBot) return;
            if (e.Emoji.GetDiscordName() != ":white_check_mark:") return;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var discord = scope.ServiceProvider.GetRequiredService<DiscordMessageService>();

            var settings = db.RulesSettings.FirstOrDefault();
            if (settings == null) return;
            if (e.Message.Id != settings.LastMessageId) return;
            if (settings.RoleId == 0) return;

            // Проверяем, есть ли уже роль у пользователя
            var guild = await sender.GetGuildAsync(e.Guild.Id);
            if (guild == null) return;

            var member = await guild.GetMemberAsync(e.User.Id);
            if (member == null) return;

            bool alreadyHasRole = member.Roles.Any(r => r.Id == settings.RoleId);

            if (alreadyHasRole)
            {
                // Роль уже есть — просто удаляем реакцию
                await discord.RemoveAcceptReactionAsync(settings.ChannelId, settings.LastMessageId, e.User.Id);
                return;
            }

            try
            {
                await discord.GrantRoleAsync(e.Guild.Id, e.User.Id, settings.RoleId);
                await discord.RemoveAcceptReactionAsync(settings.ChannelId, settings.LastMessageId, e.User.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка выдачи роли: {ex}");
            }
        }
    }
}