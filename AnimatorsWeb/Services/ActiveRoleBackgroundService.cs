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
                var next = Next1AmUtc(now);
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

        private static DateTime Next1AmUtc(DateTime now)
        {
            var target = now.Date.AddHours(1);
            if (target <= now) target = target.AddDays(1);
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
                        "Active role daily check: granted {Granted}, revoked {Revoked}, nochange {NoChange}",
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
            DateOnly cutoff;
            if (settings != null)
            {
                cutoff = ActiveRoleService.GetCleanupCutoff(settings);
            }
            else
            {
                // Если настроек нет — дефолт: 7 (окно по умолчанию) + 7 = 14 дней.
                var mskNow = DateTime.UtcNow.AddHours(3);
                cutoff = DateOnly.FromDateTime(mskNow.AddDays(-14));
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