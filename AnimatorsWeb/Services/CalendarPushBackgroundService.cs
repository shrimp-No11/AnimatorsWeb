using AnimatorsWeb.Data;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Services
{
    public class CalendarPushBackgroundService : BackgroundService
    {
        private readonly CalendarPushQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CalendarPushBackgroundService> _logger;

        public CalendarPushBackgroundService(
            CalendarPushQueue queue,
            IServiceScopeFactory scopeFactory,
            ILogger<CalendarPushBackgroundService> logger)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            await foreach (var evId in _queue.ReadAllAsync(ct))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var sync = scope.ServiceProvider.GetRequiredService<DiscordCalendarSyncService>();

                    var ev = await db.CalendarEvents.FindAsync(evId);
                    if (ev == null) continue;

                    await sync.PushAsync(ev, ct);
                    await db.SaveChangesAsync(ct);
                    await Task.Delay(300, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Calendar push failed for event {Id}", evId);
                }
            }
        }
    }
}