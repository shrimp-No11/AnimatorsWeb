using AnimatorsWeb.Data;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Services
{
    public class ThumbnailCleanupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ThumbnailCleanupService> _logger;

        public ThumbnailCleanupService(
            IServiceScopeFactory scopeFactory,
            ILogger<ThumbnailCleanupService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var next = NextWednesday4AmUtc(now);
                var delay = next - now;

                _logger.LogInformation(
                    "Thumbnail cleanup scheduled for {Next} UTC (in {Hours:F1}h)",
                    next, delay.TotalHours);

                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }

                try { await CleanupAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Thumbnail cleanup failed");
                }
            }
        }

        private static DateTime NextWednesday4AmUtc(DateTime now)
        {
            // DayOfWeek: Sunday=0, Monday=1, Tuesday=2, Wednesday=3
            int daysAhead = ((int)DayOfWeek.Wednesday - (int)now.DayOfWeek + 7) % 7;
            var target = now.Date.AddDays(daysAhead).AddHours(4);
            if (target <= now) target = target.AddDays(7);
            return target;
        }

        private async Task CleanupAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

            var folder = Path.Combine(env.WebRootPath, "featured");
            if (!Directory.Exists(folder))
            {
                _logger.LogInformation("Thumbnail cleanup: folder not found, skip");
                return;
            }

            var existingIds = (await db.Participants
                .Select(p => p.Id)
                .ToListAsync(ct))
                .ToHashSet();

            int deleted = 0;
            int kept = 0;

            foreach (var file in Directory.GetFiles(folder, "*.jpg"))
            {
                if (ct.IsCancellationRequested) break;

                var name = Path.GetFileNameWithoutExtension(file);
                if (!int.TryParse(name, out var id))
                {
                    // .gitkeep, .tmp и прочее — не трогаем
                    continue;
                }

                if (!existingIds.Contains(id))
                {
                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch { }
                }
                else
                {
                    kept++;
                }
            }

            _logger.LogInformation(
                "Thumbnail cleanup complete: deleted {Deleted}, kept {Kept}",
                deleted, kept);
        }
    }
}