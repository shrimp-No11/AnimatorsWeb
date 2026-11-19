using AnimatorsWeb.Models;

namespace AnimatorsWeb.Services
{
    /// <summary>
    /// Раскладывает правило Discord-серии на конкретные даты в заданном горизонте.
    /// Чистая функция: не трогает БД и Discord, поэтому легко тестируется.
    /// </summary>
    public static class SeriesOccurrenceCalculator
    {
        private const int MaxIterations = 2000;

        public static List<DateTime> Calculate(
            DiscordRecurrenceRule? rule,
            DateTime fromUtc,
            DateTime horizonUtc,
            IEnumerable<PresetExceptionRecord> exceptions,
            IEnumerable<DateTime> existingStarts)
        {
            var result = new List<DateTime>();
            if (rule == null || string.IsNullOrWhiteSpace(rule.Start)) return result;
            if (!DateTime.TryParse(rule.Start, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsedStart))
                return result;

            var ruleStart = DateTime.SpecifyKind(parsedStart, DateTimeKind.Utc);
            var excluded = exceptions
                .Select(e => e.OriginalStartUtc)
                .ToHashSet();
            var known = existingStarts
                .Select(d => DateTime.SpecifyKind(d, DateTimeKind.Utc))
                .ToHashSet();

            int step;
            if (rule.Frequency == 2) // weekly
            {
                step = 7 * Math.Clamp(rule.Interval, 1, 2);
                int startDow = ((int)ruleStart.DayOfWeek + 6) % 7; // Mon=0
                var baseMonday = ruleStart.Date.AddDays(-startDow);
                var weekdays = (rule.ByWeekday ?? Array.Empty<int>()).Length > 0
                    ? rule.ByWeekday!.Distinct().ToArray()
                    : new[] { startDow };

                var timeOfDay = ruleStart.TimeOfDay;
                var week = baseMonday;
                for (int i = 0; i < MaxIterations && week < horizonUtc; i++)
                {
                    foreach (var wd in weekdays)
                    {
                        if (wd < 0 || wd > 6) continue;
                        var candidate = DateTime.SpecifyKind(week.AddDays(wd) + timeOfDay, DateTimeKind.Utc);
                        if (candidate < fromUtc || candidate >= horizonUtc) continue;
                        if (excluded.Contains(candidate) || known.Contains(candidate)) continue;
                        result.Add(candidate);
                    }
                    week = week.AddDays(step);
                }
            }
            else if (rule.Frequency == 3) // daily
            {
                step = Math.Max(1, rule.Interval);
                var timeOfDay = ruleStart.TimeOfDay;
                var allowedDays = rule.ByWeekday?.ToHashSet();

                for (int i = 0; i < MaxIterations; i++)
                {
                    var day = ruleStart.Date.AddDays(i * step);
                    if (day >= horizonUtc) break;
                    var candidate = DateTime.SpecifyKind(day + timeOfDay, DateTimeKind.Utc);
                    if (candidate < fromUtc) continue;
                    if (allowedDays != null && allowedDays.Count > 0)
                    {
                        int dow = ((int)candidate.DayOfWeek + 6) % 7;
                        if (!allowedDays.Contains(dow)) continue;
                    }
                    if (excluded.Contains(candidate) || known.Contains(candidate)) continue;
                    result.Add(candidate);
                }
            }
            // monthly/yearly правила нашим builder'ом не создаются — пропускаем.

            return result.OrderBy(d => d).ToList();
        }
    }
}
