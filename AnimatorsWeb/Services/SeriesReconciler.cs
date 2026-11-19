using AnimatorsWeb.Models;

namespace AnimatorsWeb.Services
{
    /// <summary>
    /// Переводит состояние Discord-серии (правило + исключения) в список вхождений,
    /// которые должны быть карточками на сетке. Чистая логика без БД и Discord.
    /// </summary>
    public static class SeriesReconciler
    {
        public sealed record DesiredOccurrence(DateTime StartUtc, DateTime? OriginalStartUtc);

        /// <summary>
        /// Что делать с уже существующими карточками, чтобы получить <paramref name="desired"/>:
        /// какие оставить (индекс → желаемое вхождение), какие удалить и что добавить.
        /// Лишняя карточка (её нет в Discord) удаляется — включая видимые.
        /// </summary>
        public sealed record Plan(
            Dictionary<int, DesiredOccurrence> Keep,
            List<int> Remove,
            List<DesiredOccurrence> Add);

        public static Plan Build(IReadOnlyList<DateTime> existingStarts, IReadOnlyList<DesiredOccurrence> desired)
        {
            var byStart = desired.ToDictionary(d => d.StartUtc);
            var keep = new Dictionary<int, DesiredOccurrence>();
            var remove = new List<int>();
            var seen = new HashSet<DateTime>();

            for (int i = 0; i < existingStarts.Count; i++)
            {
                var start = Utc(existingStarts[i]);
                if (byStart.TryGetValue(start, out var target) && seen.Add(start))
                    keep[i] = target;
                else
                    remove.Add(i);
            }

            var add = desired.Where(d => !seen.Contains(d.StartUtc)).ToList();
            return new Plan(keep, remove, add);
        }

        public static List<DesiredOccurrence> Desired(
            DiscordRecurrenceRule? rule,
            IEnumerable<PresetExceptionRecord> exceptions,
            DateTime fromUtc,
            DateTime horizonUtc)
        {
            var result = new List<DesiredOccurrence>();
            if (rule == null) return result;

            var all = exceptions
                .Where(e => e.OriginalStartUtc > DateTime.UnixEpoch)
                .ToList();

            foreach (var slot in SeriesOccurrenceCalculator.Calculate(
                         rule, fromUtc, horizonUtc, all, Array.Empty<DateTime>()))
            {
                result.Add(new DesiredOccurrence(Utc(slot), null));
            }

            foreach (var ex in all.Where(e => !e.IsCanceled && e.NewStartUtc.HasValue))
            {
                var original = Utc(ex.OriginalStartUtc);
                var moved = Utc(ex.NewStartUtc!.Value);

                // Отменённый/перенесённый исходный слот не должен появиться дважды.
                result.RemoveAll(r => r.StartUtc == original);

                if (moved < fromUtc || moved >= horizonUtc) continue;
                if (result.All(r => r.StartUtc != moved))
                    result.Add(new DesiredOccurrence(moved, original));
            }

            return result.OrderBy(r => r.StartUtc).ToList();
        }

        private static DateTime Utc(DateTime value)
            => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
