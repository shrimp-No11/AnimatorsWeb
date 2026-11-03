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
            if (settings == null || settings.LastMessageId == 0)
            {
                Console.WriteLine("Догоняющая проверка правил: настройки не найдены, пропуск");
                return;
            }

            // Парсим AllMessageIds — все сообщения правил
            var messageIds = (settings.AllMessageIds ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => ulong.TryParse(s.Trim(), out var id) ? id : 0)
                .Where(id => id != 0)
                .ToList();

            // Если AllMessageIds пуст — fallback на LastMessageId
            if (messageIds.Count == 0)
                messageIds.Add(settings.LastMessageId);

            Console.WriteLine($"Догоняющая проверка правил: сообщений {messageIds.Count}");

            // Полная приборка: выдать роли всем, кто ставил ✅,
            // удалить все реакции ✅ со всех сообщений,
            // поставить свою ✅ на последнее
            await discord.CleanupRulesReactionsAsync(
                settings.ChannelId,
                messageIds,
                settings.LastMessageId,
                settings.RoleId,
                grantRoles: true);

            Console.WriteLine("Догоняющая проверка правил завершена");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка catch-up правил: {ex.Message}");
        }
    }
}