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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _client.ConnectAsync();

        // Догоняем реакции, пропущенные во время офлайна
        _ = Task.Run(() => CatchUpReactionsAsync(stoppingToken), stoppingToken);

        // Watchdog: если gateway умер и не оживает — принудительный рестарт контейнера
        _ = Task.Run(() => WatchdogAsync(stoppingToken), stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken);
        await _client.DisconnectAsync();
    }

    private async Task WatchdogAsync(CancellationToken ct)
    {
        // Первые 5 минут — grace period (даём gateway инициализироваться,
        // heartbeat может ещё не измеряться, реконнект после старта — нормально)
        try { await Task.Delay(TimeSpan.FromMinutes(5), ct); }
        catch (OperationCanceledException) { return; }

        var lastPing = -1;
        var lastPingChange = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);

                int currentPing;
                try { currentPing = _client.Ping; }
                catch { currentPing = -1; }

                // Ping = -1 → gateway не подключён или не измерял ещё
                if (currentPing < 0)
                {
                    var offlineMin = (DateTime.UtcNow - lastPingChange).TotalMinutes;
                    if (offlineMin > 5)
                    {
                        Console.WriteLine(
                            $"[watchdog] ping={currentPing}, gateway недоступен {offlineMin:F1} мин. Рестарт контейнера.");
                        Environment.Exit(1);
                    }
                    continue;
                }

                // Ping изменился — соединение живое
                if (currentPing != lastPing)
                {
                    lastPing = currentPing;
                    lastPingChange = DateTime.UtcNow;
                }
                else
                {
                    // Ping не менялся > 5 минут — gateway мёртв
                    var staleMin = (DateTime.UtcNow - lastPingChange).TotalMinutes;
                    if (staleMin > 5)
                    {
                        Console.WriteLine(
                            $"[watchdog] ping не менялся {staleMin:F1} мин. Рестарт контейнера.");
                        Environment.Exit(1);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[watchdog] ошибка: {ex.Message}");
            }
        }
    }
}