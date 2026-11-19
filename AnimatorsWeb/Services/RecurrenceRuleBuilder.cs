using AnimatorsWeb.Models;

namespace AnimatorsWeb.Services
{
    public class RecurrenceCalculation
    {
        public int Frequency { get; set; } = 2; // 2 = weekly
        public int Interval { get; set; } = 1;
        public int[] ByWeekday { get; set; } = Array.Empty<int>(); // 0=Mon..6=Sun
        public DateTime RuleStartUtc { get; set; }
        public List<RecurrenceException> Exceptions { get; set; } = new();
        public List<DateTime> UnmatchedOccurrences { get; set; } = new();
        /// <summary>Фактические вхождения (после правок), нужны, чтобы отличить отмену от возвращённого вхождения.</summary>
        public List<DateTime> Occurrences { get; set; } = new();
    }

    public class RecurrenceException
    {
        public DateTime OriginalStartUtc { get; set; }
        public bool IsCanceled { get; set; }
        public DateTime? NewStartUtc { get; set; }
    }

    public class RecurrenceRuleBuilder
    {
        private readonly ILogger<RecurrenceRuleBuilder> _logger;

        // окно, в котором ищем "перенос" с базового дня на другой
        private const int MatchWindowDays = 3;

        public RecurrenceRuleBuilder(ILogger<RecurrenceRuleBuilder> logger)
        {
            _logger = logger;
        }

        public RecurrenceCalculation? Build(
            IReadOnlyList<(DateTime Start, DateTime End)> occurrences,
            DateTime horizonEndUtc,
            DateTime? ruleStartUtc = null,
            IReadOnlyDictionary<DateTime, DateTime>? originalStarts = null)
        {
            if (occurrences.Count == 0) return null;

            // 1. Самый частый день недели (Discord: Mon=0..Sun=6)
            var byDow = occurrences
                .GroupBy(o => ((int)o.Start.DayOfWeek + 6) % 7)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .ToList();
            int baseDow = byDow.First().Key;

            // 2. Самое частое время (UTC) среди вхождений на базовом дне
            var byTime = occurrences
                .Where(o => ((int)o.Start.DayOfWeek + 6) % 7 == baseDow)
                .GroupBy(o => (o.Start.Hour, o.Start.Minute))
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key.Hour).ThenBy(g => g.Key.Minute)
                .ToList();

            if (byTime.Count == 0) return null;

            var baseTime = byTime.First().Key;

            // 3. Вхождения на базовом дне с базовым временем
            var baseOccurrences = occurrences
                .Where(o => ((int)o.Start.DayOfWeek + 6) % 7 == baseDow
                         && o.Start.Hour == baseTime.Hour
                         && o.Start.Minute == baseTime.Minute)
                .OrderBy(o => o.Start)
                .ToList();

            // 4. Interval — самый частый шаг в неделях между соседними baseOccurrences
            int interval = 1;
            if (baseOccurrences.Count >= 2)
            {
                var gaps = new List<int>();
                for (int i = 1; i < baseOccurrences.Count; i++)
                {
                    var days = (baseOccurrences[i].Start - baseOccurrences[i - 1].Start).TotalDays;
                    var weeks = (int)Math.Round(days / 7.0);
                    if (weeks > 0) gaps.Add(weeks);
                }
                if (gaps.Count > 0)
                {
                    interval = gaps.GroupBy(g => g)
                        .OrderByDescending(g => g.Count())
                        .ThenBy(g => g.Key)
                        .First().Key;
                }
            }

            // Discord для weekly разрешает interval только 1 или 2.
            // Пропуски "1 из 3/4 недель" выражаем как interval=1 + canceled-исключения.
            if (interval > 2) interval = 1;

            // 5. RuleStartUtc — либо зафиксированный start правила (из Discord/пресета),
            //    либо первое вхождение на базовом дне. Фиксация не даёт правилу «дрейфовать»
            //    на неделю при каждом еженедельном Apply.
            DateTime ruleStart = ruleStartUtc
                ?? (baseOccurrences.Count > 0
                    ? baseOccurrences[0].Start
                    : occurrences.OrderBy(o => o.Start).First().Start);

            // 6. Идеальный календарь
            var ideal = new List<DateTime>();
            var cursor = new DateTime(ruleStart.Year, ruleStart.Month, ruleStart.Day,
                ruleStart.Hour, ruleStart.Minute, 0, DateTimeKind.Utc);
            while (cursor < horizonEndUtc)
            {
                ideal.Add(cursor);
                cursor = cursor.AddDays(7 * interval);
            }

            // 7. Точные совпадения (по date+time)
            var idealSet = new HashSet<DateTime>(ideal);
            var realSet = new HashSet<DateTime>(occurrences.Select(o => o.Start));
            var exactMatches = ideal.Where(idealSet.Contains).Where(realSet.Contains).ToHashSet();

            var leftoversIdeal = ideal.Where(d => !exactMatches.Contains(d)).OrderBy(d => d).ToList();
            var leftoversReal = occurrences.Select(o => o.Start)
                .Where(d => !exactMatches.Contains(d))
                .OrderBy(d => d).ToList();

            // 8. Пары "ideal ↔ real" по близости
            var exceptions = new List<RecurrenceException>();
            var unmatchedReal = new List<DateTime>();
            var usedIdeal = new HashSet<DateTime>();

            foreach (var real in leftoversReal)
            {
                // Точная привязка: карточка помнит, откуда её перенесли. Это убирает
                // эвристику MatchWindowDays для любых расстояний переноса.
                if (originalStarts != null
                    && originalStarts.TryGetValue(real, out var exactOriginal)
                    && leftoversIdeal.Any(d => d == exactOriginal && !usedIdeal.Contains(d)))
                {
                    exceptions.Add(new RecurrenceException
                    {
                        OriginalStartUtc = exactOriginal,
                        IsCanceled = false,
                        NewStartUtc = real
                    });
                    usedIdeal.Add(exactOriginal);
                    continue;
                }

                var match = leftoversIdeal
                    .Where(d => !usedIdeal.Contains(d))
                    .Where(d => Math.Abs((d - real).TotalDays) <= MatchWindowDays)
                    .OrderBy(d => Math.Abs((d - real).TotalDays))
                    .FirstOrDefault();

                if (match != default)
                {
                    exceptions.Add(new RecurrenceException
                    {
                        OriginalStartUtc = match,
                        IsCanceled = false,
                        NewStartUtc = real
                    });
                    usedIdeal.Add(match);
                }
                else
                {
                    unmatchedReal.Add(real);
                }
            }

            // 9. Оставшиеся ideal без пары — отменить
            foreach (var idealDate in leftoversIdeal.Where(d => !usedIdeal.Contains(d)))
            {
                exceptions.Add(new RecurrenceException
                {
                    OriginalStartUtc = idealDate,
                    IsCanceled = true
                });
            }

            if (unmatchedReal.Count > 0)
            {
                _logger.LogWarning(
                    "RecurrenceRuleBuilder: {Count} real occurrences cannot be mapped to rule",
                    unmatchedReal.Count);
            }

            return new RecurrenceCalculation
            {
                Frequency = 2,
                Interval = interval,
                ByWeekday = new[] { baseDow },
                RuleStartUtc = ruleStart,
                Exceptions = exceptions,
                UnmatchedOccurrences = unmatchedReal,
                Occurrences = occurrences.Select(o => o.Start).OrderBy(d => d).ToList()
            };
        }
    }
}