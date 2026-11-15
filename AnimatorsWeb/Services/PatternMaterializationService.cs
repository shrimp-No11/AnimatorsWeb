using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using Microsoft.EntityFrameworkCore;
using AnimatorsWeb.Services;

namespace AnimatorsWeb.Services
{
    public class PatternMaterializationService
    {
        private readonly AppDbContext _db;
        private readonly ILogger<PatternMaterializationService> _logger;

        public const int HorizonWeeks = 13;

        public PatternMaterializationService(
            AppDbContext db,
            ILogger<PatternMaterializationService> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Материализует события паттерна от StartWeekUtc до горизонта (StartWeekUtc + HorizonWeeks).
        /// Идемпотентно: уже существующие (по PatternInstanceId + StartUtc) не дублируются.
        /// </summary>
        public async Task<int> MaterializeAsync(int patternInstanceId, CancellationToken ct = default)
        {
            var instance = await _db.PatternInstances
                .Include(i => i.EventPattern)
                    .ThenInclude(p => p!.Events)
                .FirstOrDefaultAsync(i => i.Id == patternInstanceId, ct);

            if (instance == null || instance.EventPattern == null)
                return 0;

            var pattern = instance.EventPattern;
            if (pattern.Events.Count == 0)
            {
                instance.MaterializedUntilUtc = instance.StartWeekUtc.AddDays(HorizonWeeks * 7);
                await _db.SaveChangesAsync(ct);
                return 0;
            }

            var presetIds = pattern.Events.Select(e => e.EventPresetId).Distinct().ToList();
            var presets = await _db.EventPresets
                .Where(p => presetIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p, ct);

            var existingStarts = await _db.CalendarEvents
                .Where(e => e.PatternInstanceId == instance.Id)
                .Select(e => e.StartUtc)
                .ToListAsync(ct);
            var existingSet = existingStarts.ToHashSet();

            var cycleDays = 7 * Math.Max(1, pattern.CycleWeeksCount);

            // AutoRecurring = true → горизонт HorizonWeeks недель
            // AutoRecurring = false → только один цикл
            DateTime targetEnd = pattern.AutoRecurring
                ? instance.StartWeekUtc.AddDays(HorizonWeeks * 7)
                : instance.StartWeekUtc.AddDays(cycleDays);

            int created = 0;

            for (var cycleStart = instance.StartWeekUtc; cycleStart < targetEnd; cycleStart = cycleStart.AddDays(cycleDays))
            {
                foreach (var pe in pattern.Events)
                {
                    if (!presets.TryGetValue(pe.EventPresetId, out var preset)) continue;

                    var eventDateUtc = cycleStart.AddDays(pe.WeekOffset * 7 + pe.DayOfWeek);
                    var startUtc = eventDateUtc.Add(pe.StartTimeUtc);
                    var endUtc = startUtc.AddHours(preset.DurationHours);

                    if (!existingSet.Add(startUtc)) continue;

                    _db.CalendarEvents.Add(new CalendarEvent
                    {
                        EventPresetId = preset.Id,
                        PatternInstanceId = instance.Id,
                        Title = preset.DefaultTitle,
                        Description = preset.DefaultDescription,
                        Location = preset.Location,
                        StartUtc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc),
                        EndUtc = DateTime.SpecifyKind(endUtc, DateTimeKind.Utc),
                        Source = "Site",
                        SkipDiscordSync = !preset.CreateInDiscord
                    });
                    created++;
                }
            }

            instance.MaterializedUntilUtc = targetEnd;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Pattern instance {Id} materialized: created {Created} events, horizon until {Until}",
                instance.Id, created, targetEnd);

            return created;
        }

        /// <summary>
        /// Для всех активных instance догенерирует события до горизонта (rolling extend).
        /// </summary>
        public async Task<int> ExtendAllActiveAsync(CancellationToken ct = default)
        {
            var activeInstances = await _db.PatternInstances
                .Where(i => i.IsActive)
                .Select(i => i.Id)
                .ToListAsync(ct);

            int totalCreated = 0;
            foreach (var id in activeInstances)
            {
                try { totalCreated += await MaterializeAsync(id, ct); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extend pattern instance {Id}", id);
                }
            }
            return totalCreated;
        }
    }
}