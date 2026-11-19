using System;

namespace AnimatorsWeb.Services
{
    /// <summary>
    /// При преобразовании пресета в Discord-серию нельзя удалять уже существующее
    /// событие и создавать вместо него новое. Discord разрешает PATCH с
    /// recurrence_rule, поэтому одно из существующих событий может стать
    /// основной серией — с сохранением ID, подписчиков и обложки.
    /// </summary>
    public static class SeriesAdoption
    {
        public sealed record Candidate(ulong DiscordEventId, DateTime StartUtc, string? Source);

        /// <summary>
        /// Выбирает событие, ближайшее к началу правила. При равенстве предпочитает
        /// событие, пришедшее из Discord/созданное вручную, а не сгенерированное сайтом.
        /// </summary>
        public static Candidate? Pick(IReadOnlyList<Candidate> candidates, DateTime ruleStartUtc)
        {
            if (candidates == null || candidates.Count == 0) return null;

            var start = Utc(ruleStartUtc);

            return candidates
                .OrderBy(c => Math.Abs((Utc(c.StartUtc) - start).TotalSeconds))
                .ThenBy(c => string.Equals(c.Source, "Site", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(c => Utc(c.StartUtc))
                .First();
        }

        private static DateTime Utc(DateTime value)
            => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
