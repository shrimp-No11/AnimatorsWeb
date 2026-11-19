using AnimatorsWeb.Models;

namespace AnimatorsWeb.Services
{
    /// <summary>
    /// Чистая логика слияния исключений Discord-серии. Вынесена из ApplyService,
    /// чтобы покрыть тестами без обращений к Discord и БД.
    /// </summary>
    public static class SeriesExceptionDiff
    {
        /// <summary>Сколько дней истории исключений держим (совпадает с уборкой БД).</summary>
        public const int RetentionDays = 64;

        /// <summary>
        /// Итоговый набор исключений: свежие из RecurrenceRuleBuilder + ещё актуальные
        /// исключения из сохранённого состояния (за горизонтом builder'а).
        /// </summary>
        /// <param name="calculated">Исключения, посчитанные из текущих вхождений.</param>
        /// <param name="previous">Исключения, сохранённые в пресете после прошлого Apply.</param>
        /// <param name="occurrenceStarts">Фактические даты вхождений серии.</param>
        /// <param name="now">Текущее время UTC.</param>
        public static List<PresetExceptionRecord> Merge(
            IEnumerable<RecurrenceException> calculated,
            IEnumerable<PresetExceptionRecord> previous,
            IEnumerable<DateTime> occurrenceStarts,
            DateTime now,
            bool preservePrevious = false)
        {
            var result = calculated
                .Select(e => new PresetExceptionRecord
                {
                    OriginalStartUtc = e.OriginalStartUtc,
                    IsCanceled = e.IsCanceled,
                    NewStartUtc = e.NewStartUtc
                })
                .ToList();

            // ВАЖНО: для активной серии исключения должны вычисляться ИСКЛЮЧИТЕЛЬНО из
            // текущих карточек. Старые отмены, сохранённые в пресете, НЕ переносятся:
            // если модератор заново расставил карточки, все предыдущие отмены должны
            // исчезнуть. Иначе Apply отменяет только что созданные события (см. логи 17:30).
            if (preservePrevious)
            {
                var knownDates = result.Select(e => e.OriginalStartUtc).ToHashSet();
                var realStarts = occurrenceStarts.ToHashSet();
                foreach (var prev in previous)
                {
                    if (prev.OriginalStartUtc <= DateTime.UnixEpoch) continue;
                    if (knownDates.Contains(prev.OriginalStartUtc)) continue;
                    if (realStarts.Contains(prev.OriginalStartUtc)) continue;
                    if (prev.OriginalStartUtc < now.AddDays(-RetentionDays)) continue;
                    result.Add(prev);
                }
            }

            return result.OrderBy(e => e.OriginalStartUtc).ToList();
        }

        /// <summary>
        /// Нужно ли перезаписывать исключение в Discord. POST с тем же
        /// original_scheduled_start_time полностью заменяет исключение, поэтому
        /// смена типа (отмена ↔ перенос) и времени обрабатывается им же.
        /// </summary>
        public static bool NeedsUpdate(
            DiscordExceptionInfo current,
            PresetExceptionRecord desired,
            bool ruleChanged = false)
            => ruleChanged
               || current.IsCanceled != desired.IsCanceled
               || Normalize(current.NewStartUtc) != Normalize(desired.NewStartUtc);

        private static DateTime? Normalize(DateTime? value)
            => value?.ToUniversalTime();
    }
}
