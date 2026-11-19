namespace AnimatorsWeb.Services
{
    /// <summary>
    /// Одноразовый Pull при старте приложения (через 20 секунд, когда бот уже подключён).
    /// Оперативные изменения ловит gateway-хендлер, принудительное обновление —
    /// кнопка «Обновить из Discord». Периодический Pull убран как избыточный и дорогой.
    /// </summary>
    public class DiscordCalendarPullBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DiscordCalendarPullBackgroundService> _logger;

        public DiscordCalendarPullBackgroundService(
            IServiceScopeFactory scopeFactory,
            ILogger<DiscordCalendarPullBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            // Даём боту время подключиться к gateway после старта.
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<DiscordCalendarSyncService>();
                await sync.PullAsync(ct: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Startup Discord calendar pull failed");
            }
        }
    }
}
