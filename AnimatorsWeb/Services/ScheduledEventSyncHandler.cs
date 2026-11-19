using DSharpPlus;
using DSharpPlus.EventArgs;

namespace AnimatorsWeb.Services
{
    /// <summary>
    /// Реагирует на gateway-события Discord: создание/изменение/удаление scheduled event.
    /// Это даёт своевременную синхронизацию без захода модератора на страницу календаря.
    /// DSharpPlus не отдаёт recurrence_rule и exceptions в объекте события, поэтому
    /// точечный Pull дополнительно читает событие через raw REST (GET /scheduled-events/{id}).
    ///
    /// Плюс страховка на переподключение: события, случившиеся пока бот был офлайн,
    /// gateway не повторяет, поэтому после реконнекта делаем один общий Pull.
    /// </summary>
    public class ScheduledEventSyncHandler :
        IEventHandler<ScheduledGuildEventUpdatedEventArgs>,
        IEventHandler<ScheduledGuildEventCreatedEventArgs>,
        IEventHandler<ScheduledGuildEventDeletedEventArgs>,
        IEventHandler<SessionCreatedEventArgs>,
        IEventHandler<SessionResumedEventArgs>
    {
        private static readonly TimeSpan CatchUpInterval = TimeSpan.FromMinutes(5);
        private static long _lastCatchUpTicks;
        private static int _sessionsSeen;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ScheduledEventSyncHandler> _logger;

        public ScheduledEventSyncHandler(
            IServiceScopeFactory scopeFactory,
            ILogger<ScheduledEventSyncHandler> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public Task HandleEventAsync(DiscordClient sender, ScheduledGuildEventUpdatedEventArgs args)
            => SyncAsync(args.EventAfter?.Id);

        public Task HandleEventAsync(DiscordClient sender, ScheduledGuildEventCreatedEventArgs args)
            => SyncAsync(args.Event?.Id);

        public Task HandleEventAsync(DiscordClient sender, ScheduledGuildEventDeletedEventArgs args)
            => DeleteAsync(args.Event?.Id);

        // Первый SessionCreated — обычный старт (его покрывает Pull после запуска).
        // Следующий — новая сессия после разрыва: пропущенные события gateway не повторяет.
        public Task HandleEventAsync(DiscordClient sender, SessionCreatedEventArgs args)
            => Interlocked.Increment(ref _sessionsSeen) > 1
                ? CatchUpAsync()
                : Task.CompletedTask;

        // Resumed — сессия восстановлена. Discord обычно догоняет события сам, но общий
        // Pull закрывает дыры (например, изменения exception у recurring-серии).
        public Task HandleEventAsync(DiscordClient sender, SessionResumedEventArgs args)
            => CatchUpAsync();

        /// <summary>
        /// Общий Pull после переподключения. Не чаще раза в 5 минут и не блокирует
        /// gateway: тяжёлые сетевые запросы уходят в фон.
        /// </summary>
        private Task CatchUpAsync()
        {
            var now = DateTime.UtcNow.Ticks;
            var previous = Interlocked.Read(ref _lastCatchUpTicks);
            if (now - previous < CatchUpInterval.Ticks) return Task.CompletedTask;
            if (Interlocked.CompareExchange(ref _lastCatchUpTicks, now, previous) != previous)
                return Task.CompletedTask;

            CalendarTrace.Write("GATEWAY", "reconnected — catch-up pull");
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var sync = scope.ServiceProvider.GetRequiredService<DiscordCalendarSyncService>();
                    await sync.PullAsync(ct: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Gateway catch-up pull failed");
                }
            });

            return Task.CompletedTask;
        }

        private async Task SyncAsync(ulong? discordEventId)
        {
            if (!discordEventId.HasValue) return;
            CalendarTrace.Write("GATEWAY", $"event {discordEventId.Value} changed in Discord");
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<DiscordCalendarSyncService>();
                await sync.SyncOneAsync(discordEventId.Value);
                _logger.LogDebug("Gateway: pulled Discord event {Id}", discordEventId.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gateway sync failed for event {Id}", discordEventId.Value);
            }
        }

        private async Task DeleteAsync(ulong? discordEventId)
        {
            if (!discordEventId.HasValue) return;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<DiscordCalendarSyncService>();
                await sync.DeleteByDiscordIdAsync(discordEventId.Value);
                _logger.LogDebug("Gateway: removed Discord event {Id}", discordEventId.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gateway delete failed for event {Id}", discordEventId.Value);
            }
        }
    }
}
