using AnimatorsWeb.Models;

namespace AnimatorsWeb.Services
{
    /// <summary>
    /// Планировщик Discord-серии. Главный принцип календаря:
    /// <b>сетка модератора — эталон</b>, Discord обязан показать ровно то же самое.
    ///
    /// Поэтому правило (частота, интервал, день недели, время) берётся из настроек пресета,
    /// а конкретная расстановка карточек превращается в исключения:
    ///  - карточка стоит на идеальном слоте правила  → ничего;
    ///  - карточки на слоте нет                       → отмена (is_canceled);
    ///  - карточка перенесена                         → перенос (original → new).
    ///
    /// Интервал больше НЕ угадывается по «дыркам» в сетке: именно из-за этого
    /// три карточки превращались в «раз в две недели» и терялись события.
    /// </summary>
    public static class SeriesRulePlanner
    {
        /// <summary>Насколько далеко от идеального слота может стоять карточка, чтобы считаться переносом.</summary>
        public const int MatchWindowDays = 7;

        public static RecurrenceCalculation? Build(
            EventPreset preset,
            IReadOnlyList<(DateTime Start, DateTime? OriginalStart)> cards,
            DateTime horizonUtc)
        {
            if (cards.Count == 0) return null;

            int frequency = preset.RecurrenceFrequency is 3 ? 3 : 2; // weekly по умолчанию
            int interval = frequency == 3
                ? Math.Max(1, preset.RecurrenceInterval)
                : Math.Clamp(preset.RecurrenceInterval > 0 ? preset.RecurrenceInterval : 1, 1, 2);

            var slots = cards
                .Select(c => (Start: Normalize(c.Start), OriginalStart: c.OriginalStart.HasValue
                    ? Normalize(c.OriginalStart.Value)
                    : (DateTime?)null))
                .OrderBy(c => c.Start)
                .ToList();

            int baseDow = ConfiguredWeekday(preset)
                          ?? (((int)slots[0].OriginalStart.GetValueOrDefault(slots[0].Start).DayOfWeek + 6) % 7);

            var timeOfDay = preset.DefaultStartTimeUtc ?? slots[0].Start.TimeOfDay;
            var earliest = slots.Min(c => c.OriginalStart.HasValue && c.OriginalStart.Value < c.Start
                ? c.OriginalStart.Value
                : c.Start);

            var ruleStart = ResolveRuleStart(preset, earliest, baseDow, timeOfDay, frequency, interval);
            var ideal = BuildIdeal(ruleStart, horizonUtc, frequency, interval, timeOfDay);

            // Идеальные слоты в далёком прошлом не нужны: по ним нечего отменять.
            var from = earliest.AddDays(-MatchWindowDays);
            ideal = ideal.Where(d => d >= from).ToList();

            var idealSet = ideal.ToHashSet();
            var used = new HashSet<DateTime>();
            var exceptions = new List<RecurrenceException>();
            var unmatched = new List<DateTime>();

            // 1) Карточки, которые уже стоят ровно на слоте правила, забирают свой слот первыми.
            foreach (var card in slots)
            {
                if (card.OriginalStart.HasValue) continue;
                if (idealSet.Contains(card.Start) && used.Add(card.Start))
                {
                    // точное попадание — исключение не нужно
                }
            }

            // 2) Остальные карточки: исходный слот (после переноса) → текущая дата.
            foreach (var card in slots)
            {
                if (card.OriginalStart is { } orig && idealSet.Contains(orig) && used.Add(orig))
                {
                    if (orig != card.Start)
                    {
                        exceptions.Add(new RecurrenceException
                        {
                            OriginalStartUtc = orig,
                            IsCanceled = false,
                            NewStartUtc = card.Start
                        });
                    }
                    continue;
                }

                if (card.OriginalStart == null && used.Contains(card.Start)) continue;

                // Старые данные без OriginalStartUtc: ищем ближайший свободный слот.
                var candidate = ideal
                    .Where(d => !used.Contains(d) && Math.Abs((d - card.Start).TotalDays) <= MatchWindowDays)
                    .OrderBy(d => Math.Abs((d - card.Start).TotalDays))
                    .ThenBy(d => d)
                    .FirstOrDefault();

                if (candidate == default)
                {
                    // Карточку невозможно выразить через правило Discord — сообщаем вызывающему.
                    unmatched.Add(card.Start);
                    continue;
                }

                used.Add(candidate);
                if (candidate != card.Start)
                {
                    exceptions.Add(new RecurrenceException
                    {
                        OriginalStartUtc = candidate,
                        IsCanceled = false,
                        NewStartUtc = card.Start
                    });
                }
            }

            // 3) Слоты правила, на которых нет карточки, — отмены.
            foreach (var slot in ideal.Where(d => !used.Contains(d)))
            {
                exceptions.Add(new RecurrenceException
                {
                    OriginalStartUtc = slot,
                    IsCanceled = true
                });
            }

            return new RecurrenceCalculation
            {
                Frequency = frequency,
                Interval = interval,
                ByWeekday = new[] { baseDow },
                RuleStartUtc = ruleStart,
                Exceptions = exceptions.OrderBy(e => e.OriginalStartUtc).ToList(),
                UnmatchedOccurrences = unmatched,
                Occurrences = slots.Select(c => c.Start).OrderBy(d => d).ToList()
            };
        }

        /// <summary>Строит идеальные слоты правила от ruleStart до горизонта.</summary>
        public static List<DateTime> BuildIdeal(
            DateTime ruleStart, DateTime horizonUtc, int frequency, int interval, TimeSpan timeOfDay)
        {
            var result = new List<DateTime>();
            var cursor = DateTime.SpecifyKind(ruleStart.Date + timeOfDay, DateTimeKind.Utc);
            int step = frequency == 3 ? Math.Max(1, interval) : 7 * Math.Max(1, interval);

            for (int guard = 0; guard < 4000 && cursor < horizonUtc; guard++)
            {
                result.Add(cursor);
                cursor = DateTime.SpecifyKind(cursor.AddDays(step), DateTimeKind.Utc);
            }
            return result;
        }

        private static DateTime ResolveRuleStart(
            EventPreset preset, DateTime earliest, int dow, TimeSpan timeOfDay, int frequency, int interval)
        {
            // Сохранённый start задаёт фазу двухнедельного интервала — сохраняем его,
            // если он не позже первой карточки и совпадает по дню недели/времени.
            if (TryParseStoredStart(preset.RecurrenceRuleJson, out var stored)
                && stored <= earliest
                && stored.TimeOfDay == timeOfDay
                && (frequency != 2 || ((int)stored.DayOfWeek + 6) % 7 == dow))
            {
                return stored;
            }

            return AlignBack(earliest, dow, timeOfDay, frequency, interval);
        }

        private static DateTime AlignBack(
            DateTime start, int dow, TimeSpan timeOfDay, int frequency, int interval)
        {
            var atTime = DateTime.SpecifyKind(start.Date + timeOfDay, DateTimeKind.Utc);

            if (frequency == 3)
            {
                var step = Math.Max(1, interval);
                while (atTime > start) atTime = atTime.AddDays(-step);
                return atTime;
            }

            var delta = (((int)atTime.DayOfWeek + 6) % 7 - dow + 7) % 7;
            var aligned = DateTime.SpecifyKind(atTime.AddDays(-delta), DateTimeKind.Utc);
            var weeks = Math.Max(1, interval);
            while (aligned > start) aligned = aligned.AddDays(-7 * weeks);
            return aligned;
        }

        private static int? ConfiguredWeekday(EventPreset preset)
        {
            var parts = preset.RecurrenceByWeekdays?.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts is { Length: > 0 }
                && int.TryParse(parts[0], out var dow)
                && dow is >= 0 and <= 6)
                return dow;
            return null;
        }

        private static bool TryParseStoredStart(string? json, out DateTime value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("start", out var el)
                    || el.ValueKind != System.Text.Json.JsonValueKind.String)
                    return false;
                if (!DateTime.TryParse(el.GetString(), null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal |
                        System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var dt))
                    return false;
                value = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                return true;
            }
            catch { return false; }
        }

        private static DateTime Normalize(DateTime value)
            => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
