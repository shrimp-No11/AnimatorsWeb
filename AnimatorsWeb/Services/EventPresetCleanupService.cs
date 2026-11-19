using AnimatorsWeb.Data;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Services
{
    public class EventPresetCleanupService
    {
        private readonly AppDbContext _db;
        private readonly ILogger<EventPresetCleanupService> _logger;

        public EventPresetCleanupService(
            AppDbContext db,
            ILogger<EventPresetCleanupService> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Удаляет одиночные пресеты (IsDiscordSeries=false), у которых:
        ///  - есть хотя бы один CalendarEvent,
        ///  - все CalendarEvents уже прошли (EndUtc &lt; now).
        /// Серии и пустые пресеты не трогает.
        /// </summary>
        public async Task<int> RemoveDetachedArtifactsAsync(CancellationToken ct = default)
        {
            var detached = await _db.CalendarEvents
                .Where(e => e.SkipDiscordSync && e.OriginalStartUtc.HasValue)
                .ToListAsync(ct);
            if (detached.Count == 0) return 0;
            _db.CalendarEvents.RemoveRange(detached);
            await _db.SaveChangesAsync(ct);
            return detached.Count;
        }

        public async Task<int> CleanupAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var pastBufferCutoff = now.AddDays(-64);

            // Устаревшие «отцепленные» карточки (от старого механизма переноса):
            // перенесённые вхождения теперь рисуются из исключений, они не нужны.
            await RemoveDetachedArtifactsAsync(ct);

            // 1) Удаляем прошедшие CalendarEvent (старше 64 дней от окончания)
            var oldEvents = await _db.CalendarEvents
                .Where(e => e.EndUtc < pastBufferCutoff)
                .ToListAsync(ct);

            if (oldEvents.Count > 0)
            {
                _db.CalendarEvents.RemoveRange(oldEvents);
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation("Calendar event cleanup: removed {Count} past events", oldEvents.Count);
            }

            // 2) Удаляем одиночные пресеты, у которых все события в прошлом.
            //    Пресеты, используемые паттернами, трогать нельзя — иначе каскад убьёт PatternEvent.
            var usedByPatterns = await _db.PatternEvents
                .Select(pe => pe.EventPresetId)
                .ToListAsync(ct);
            var candidates = await _db.EventPresets
                .Where(p => !p.IsDiscordSeries && !p.IsDeleted && !usedByPatterns.Contains(p.Id))
                .Select(p => new
                {
                    p.Id,
                    HasEvents = _db.CalendarEvents.Any(e => e.EventPresetId == p.Id),
                    HasFuture = _db.CalendarEvents.Any(e => e.EventPresetId == p.Id && e.EndUtc >= now),
                    HasPast = _db.CalendarEvents.Any(e => e.EventPresetId == p.Id && e.EndUtc < now)
                })
                .Where(x => x.HasEvents && !x.HasFuture && x.HasPast)
                .ToListAsync(ct);

            if (candidates.Count == 0) return 0;

            var ids = candidates.Select(c => c.Id).ToList();

            var events = await _db.CalendarEvents
                .Where(e => e.EventPresetId != null && ids.Contains(e.EventPresetId.Value))
                .ToListAsync(ct);

            _db.CalendarEvents.RemoveRange(events);

            var presets = await _db.EventPresets
                .Where(p => ids.Contains(p.Id))
                .ToListAsync(ct);

            _db.EventPresets.RemoveRange(presets);

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Preset cleanup: removed {Presets} presets, {Events} events",
                presets.Count, events.Count);

            return presets.Count;
        }
    }
}