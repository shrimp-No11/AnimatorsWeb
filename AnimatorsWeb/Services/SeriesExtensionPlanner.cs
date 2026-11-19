using AnimatorsWeb.Models;

namespace AnimatorsWeb.Services
{
    /// <summary>
    /// «Продлить»: какие новые вхождения серии нужно показать на сетке.
    /// Слоты берутся из правила серии, а не из «кликнутой» карточки — иначе
    /// перенесённое вхождение сдвигало бы всю будущую сетку.
    /// </summary>
    public static class SeriesExtensionPlanner
    {
        public const int DefaultCount = 12;

        /// <param name="cards">Все карточки пресета, включая скрытые.</param>
        public static List<DateTime> NextSlots(
            EventPreset preset,
            IReadOnlyCollection<(DateTime StartUtc, DateTime? OriginalStartUtc, bool IsHidden)> cards,
            int count = DefaultCount)
        {
            var visible = cards
                .Where(c => !c.IsHidden)
                .Select(c => (StartUtc: Utc(c.StartUtc), OriginalStartUtc: c.OriginalStartUtc))
                .ToList();

            if (visible.Count == 0 || count <= 0) return new List<DateTime>();

            var horizon = visible.Max(c => c.StartUtc).AddDays(7 * 13);
            var calc = SeriesRulePlanner.Build(preset, visible, horizon);
            if (calc == null) return new List<DateTime>();

            var rule = new DiscordRecurrenceRule
            {
                Start = calc.RuleStartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Frequency = calc.Frequency,
                Interval = calc.Interval,
                ByWeekday = calc.ByWeekday
            };

            // Занятыми считаем и текущие даты карточек, и их исходные слоты:
            // перенесённая карточка закрывает свой слот. Скрытые «надгробия»
            // занятыми не считаются — их оживляет вызывающая сторона.
            var busy = cards
                .Where(c => !c.IsHidden)
                .SelectMany(c => c.OriginalStartUtc.HasValue
                    ? new[] { Utc(c.StartUtc), Utc(c.OriginalStartUtc.Value) }
                    : new[] { Utc(c.StartUtc) })
                .ToHashSet();

            var last = busy.Max();
            var from = last.AddDays(1);
            var until = last.AddDays(7 * Math.Clamp(calc.Interval, 1, 2) * 13 + 8);

            return SeriesOccurrenceCalculator
                .Calculate(rule, from, until,
                    Array.Empty<PresetExceptionRecord>(), Array.Empty<DateTime>())
                .Where(s => !busy.Contains(Utc(s)))
                .Take(count)
                .Select(Utc)
                .ToList();
        }

        private static DateTime Utc(DateTime value)
            => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
