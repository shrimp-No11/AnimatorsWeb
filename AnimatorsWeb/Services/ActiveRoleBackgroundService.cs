using AnimatorsWeb.Data;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Services
{
    public class ActiveRoleBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ActiveRoleBackgroundService> _logger;

        public ActiveRoleBackgroundService(
            IServiceScopeFactory scopeFactory,
            ILogger<ActiveRoleBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var next = NextWednesday1AmUtc(now);
                var delay = next - now;

                _logger.LogInformation(
                    "Active role check scheduled for {Next} UTC (in {Hours:F1}h)",
                    next, delay.TotalHours);

                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }

                try { await RunCheckAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Active role background check failed");
                }
            }
        }

        // 01:00 UTC = 04:00 МСК (UTC+3). Среда.
        private static DateTime NextWednesday1AmUtc(DateTime now)
        {
            int daysAhead = ((int)DayOfWeek.Wednesday - (int)now.DayOfWeek + 7) % 7;
            var target = now.Date.AddDays(daysAhead).AddHours(1);
            if (target <= now) target = target.AddDays(7);
            return target;
        }

        private async Task RunCheckAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var service = scope.ServiceProvider.GetRequiredService<ActiveRoleService>();

            var settings = await db.ActiveRoleSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (settings == null || !settings.IsEnabled)
            {
                _logger.LogInformation("Active role check skipped: disabled or not configured");
            }
            else
            {
                var result = await service.ApplyAsync();

                if (result.Success)
                {
                    _logger.LogInformation(
                        "Active role weekly check: granted {Granted}, revoked {Revoked}, nochange {NoChange}",
                        result.WillGrant, result.WillRevoke, result.NoChange);
                }
                else
                {
                    _logger.LogWarning("Active role weekly check failed: {Error}", result.Error);
                }
            }

            // Чистка старых UserActivity
            await CleanupActivitiesAsync(db, settings, ct);
        }

        private async Task CleanupActivitiesAsync(
            AppDbContext db,
            AnimatorsWeb.Models.ActiveRoleSettings? settings,
            CancellationToken ct)
        {
            // Храним max(все окна) + 14 дней. Если settings нет — по умолчанию 30+14 = 44 дня.
            DateOnly cutoff;
            if (settings != null)
            {
                cutoff = ActiveRoleService.GetCleanupCutoff(settings);
            }
            else
            {
                // Если настроек нет — дефолт: 30 + 14 = 44 дня.
                var mskNow = DateTime.UtcNow.AddHours(3);
                cutoff = DateOnly.FromDateTime(mskNow.AddDays(-44));
            }

            var deleted = await db.UserActivities
                .Where(a => a.Date < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "UserActivity cleanup: deleted {Count} rows older than {Cutoff}",
                    deleted, cutoff);
            }
        }
    }
}