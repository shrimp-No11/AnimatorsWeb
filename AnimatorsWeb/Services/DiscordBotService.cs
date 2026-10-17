using AnimatorsWeb.Data;
using DSharpPlus;
using DSharpPlus.Clients;
using DSharpPlus.EventArgs;
using Microsoft.Extensions.DependencyInjection;

public class DiscordBotService : BackgroundService
{
    private readonly DiscordClient _client;
    private readonly IEventDispatcher _dispatcher;
    private readonly IServiceScopeFactory _scopeFactory;

    public DiscordBotService(DiscordClient client, IEventDispatcher dispatcher, IServiceScopeFactory scopeFactory)
    {
        _client = client;
        _dispatcher = dispatcher;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _client.ConnectAsync();

        // Догоняем реакции, пропущенные во время офлайна
        _ = Task.Run(() => CatchUpReactionsAsync(stoppingToken), stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken);
        await _client.DisconnectAsync();




    }
    private async Task CatchUpReactionsAsync(CancellationToken ct)
    {
        // Небольшая задержка, чтобы бот точно был готов
        await Task.Delay(3000, ct);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var discord = scope.ServiceProvider.GetRequiredService<DiscordMessageService>();

            var settings = db.RulesSettings.FirstOrDefault();
            if (settings == null || settings.LastMessageId == 0 || settings.RoleId == 0) return;

            // Получаем всех, кто поставил ✅
            var reactors = await discord.GetAcceptReactorsAsync(settings.ChannelId, settings.LastMessageId);

            Console.WriteLine($"Догоняющая проверка реакций: {reactors.Count} пользователей");

            foreach (var userId in reactors)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    await discord.GrantRoleAsync(settings.GuildId, userId, settings.RoleId);
                    await discord.RemoveAcceptReactionAsync(settings.ChannelId, settings.LastMessageId, userId);

                    // Небольшая пауза, чтобы не спамить API
                    await Task.Delay(200, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка выдачи роли {userId}: {ex.Message}");
                }
            }

            // Восстанавливаем свою реакцию (если удалилась)
            await discord.AddAcceptReactionAsync(settings.ChannelId, settings.LastMessageId);

            Console.WriteLine("Догоняющая проверка завершена");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка catch-up: {ex.Message}");
        }
    }
}