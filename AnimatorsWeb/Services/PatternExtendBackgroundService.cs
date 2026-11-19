using AnimatorsWeb.Data;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Services
{
    public class PatternExtendBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PatternExtendBackgroundService> _logger;

        public PatternExtendBackgroundService(
            IServiceScopeFactory scopeFactory,
            ILogger<PatternExtendBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var next = ComputeNextRunUtc(DateTime.UtcNow);
                var delay = next - DateTime.UtcNow;
                _logger.LogInformation(
                    "Weekly calendar job scheduled for {Next} UTC (in {Hours:F1}h)",
                    next, delay.TotalHours);

                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { break; }

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var sp = scope.ServiceProvider;

                    var materialization = sp.GetRequiredService<PatternMaterializationService>();
                    var created = await materialization.ExtendAllActiveAsync(ct);
                    _logger.LogInformation("Pattern extend: materialized {Created} events", created);

                    var seriesExtender = sp.GetRequiredService<SeriesOccurrenceExtender>();
                    var seriesCreated = await seriesExtender.ExtendAllAsync(ct);
                    _logger.LogInformation("Series extend: materialized {Created} occurrences", seriesCreated);

                    var apply = sp.GetRequiredService<DiscordCalendarApplyService>();
                    var plan = await apply.ApplyAsync(ct);
                    _logger.LogInformation(
                        "Weekly apply: created {C}, updated {U}, deleted {D}",
                        plan.ToCreate, plan.ToUpdate, plan.ToDelete);

                    var cleanup = sp.GetRequiredService<EventPresetCleanupService>();
                    var removed = await cleanup.CleanupAsync(ct);
                    _logger.LogInformation("Preset cleanup: removed {Count} presets", removed);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Weekly calendar job failed");
                }
            }
        }

        /// <summary>Вторник 02:15 МСК = понедельник 23:15 UTC, без случайной задержки.</summary>
        private static DateTime ComputeNextRunUtc(DateTime nowUtc)
            => CalendarJobSchedule.NextRunUtc(nowUtc);
    }
}