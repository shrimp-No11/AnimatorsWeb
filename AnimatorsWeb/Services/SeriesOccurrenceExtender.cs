using System.Text.Json;
using AnimatorsWeb.Data;
using AnimatorsWeb.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimatorsWeb.Services
{
    /// <summary>
    /// Догенерирует конкретные вхождения drag-серий (пресетов с IsDiscordSeries),
    /// чтобы серия не заканчивалась через 13 недель после «Размножить».
    /// Источник правила — EventPreset.RecurrenceRuleJson (уже вычисленный Apply),
    /// фолбэк — пересчёт из существующих вхождений через RecurrenceRuleBuilder.
    /// </summary>
    public class SeriesOccurrenceExtender
    {
        public const int HorizonWeeks = 13;

        private readonly AppDbContext _db;
        private readonly ILogger<SeriesOccurrenceExtender> _logger;
        private readonly ILoggerFactory _loggerFactory;

        public SeriesOccurrenceExtender(
            AppDbContext db,
            ILogger<SeriesOccurrenceExtender> logger,
            ILoggerFactory loggerFactory)
        {
            _db = db;
            _logger = logger;
            _loggerFactory = loggerFactory;
        }

        public async Task<int> ExtendAllAsync(CancellationToken ct = default)
        {
            var presets = await _db.EventPresets
                .Where(p => p.IsDiscordSeries && !p.IsDeleted)
                .ToListAsync(ct);

            int total = 0;
            foreach (var preset in presets)
            {
                try { total += await ExtendAsync(preset, ct); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extend series preset {Id}", preset.Id);
                }
            }
            return total;
        }

        public async Task<int> ExtendAsync(EventPreset preset, CancellationToken ct = default)
        {
            // Незавершённые локальные правки не трогаем: их отправит Apply.
            if (preset.PendingApply) return 0;

            var now = DateTime.UtcNow;
            var horizon = now.AddDays(HorizonWeeks * 7);

            var occurrences = await _db.CalendarEvents
                .Where(e => e.EventPresetId == preset.Id && !e.IsHidden)
                .OrderBy(e => e.StartUtc)
                .Select(e => new { e.StartUtc, e.EndUtc, e.OriginalStartUtc })
                .ToListAsync(ct);

            // Нет вхождений — серия ещё не создана или приостановлена (все отменены).
            if (occurrences.Count == 0) return 0;

            var exceptions = ParseExceptions(preset.RecurrenceExceptionsJson);
            var rule = ParseRule(preset.RecurrenceRuleJson);

            if (rule == null)
            {
                var calc = SeriesRulePlanner.Build(
                    preset,
                    occurrences.Select(o => (o.StartUtc, o.OriginalStartUtc)).ToList(),
                    horizon);
                if (calc == null) return 0;

                rule = new DiscordRecurrenceRule
                {
                    Start = calc.RuleStartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    Frequency = calc.Frequency,
                    Interval = calc.Interval,
                    ByWeekday = calc.ByWeekday
                };
                // Свежепосчитанные исключения важнее сохранённых (могут быть сняты).
                exceptions = calc.Exceptions.Select(e => new PresetExceptionRecord
                {
                    OriginalStartUtc = e.OriginalStartUtc,
                    IsCanceled = e.IsCanceled,
                    NewStartUtc = e.NewStartUtc
                }).ToList();
            }

            var starts = SeriesOccurrenceCalculator.Calculate(
                rule, now, horizon, exceptions, occurrences.Select(o => o.StartUtc));

            if (starts.Count == 0) return 0;

            foreach (var start in starts)
            {
                _db.CalendarEvents.Add(new CalendarEvent
                {
                    EventPresetId = preset.Id,
                    Title = preset.DefaultTitle,
                    Description = preset.DefaultDescription,
                    Location = preset.Location,
                    StartUtc = DateTime.SpecifyKind(start, DateTimeKind.Utc),
                    EndUtc = DateTime.SpecifyKind(start.AddHours(preset.DurationHours), DateTimeKind.Utc),
                    Source = "Site",
                    SkipDiscordSync = !preset.CreateInDiscord
                });
            }

            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Series preset {Id}: materialized {Count} occurrences until {Until}",
                preset.Id, starts.Count, horizon);
            return starts.Count;
        }

        private static DiscordRecurrenceRule? ParseRule(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var rule = new DiscordRecurrenceRule
                {
                    Frequency = root.TryGetProperty("frequency", out var f) ? f.GetInt32() : 2,
                    Interval = root.TryGetProperty("interval", out var i) && i.GetInt32() > 0 ? i.GetInt32() : 1
                };
                if (root.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.String)
                    rule.Start = s.GetString() ?? "";
                if (root.TryGetProperty("by_weekday", out var w) && w.ValueKind == JsonValueKind.Array)
                    rule.ByWeekday = w.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                return string.IsNullOrEmpty(rule.Start) ? null : rule;
            }
            catch { return null; }
        }

        private static List<PresetExceptionRecord> ParseExceptions(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try
            {
                return JsonSerializer.Deserialize<List<PresetExceptionRecord>>(json) ?? new();
            }
            catch { return new(); }
        }
    }
}
