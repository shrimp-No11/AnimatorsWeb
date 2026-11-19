namespace AnimatorsWeb.Services
{
    /// <summary>Чистая математика скользящего горизонта паттерна.</summary>
    public static class PatternCycleMath
    {
        public static (DateTime FirstCycle, DateTime TargetEnd) Compute(
            DateTime startWeekUtc,
            int cycleDays,
            bool autoRecurring,
            int horizonWeeks,
            DateTime now)
        {
            if (cycleDays <= 0) cycleDays = 7;

            if (!autoRecurring)
                return (startWeekUtc, startWeekUtc.AddDays(cycleDays));

            // Горизонт скользит вместе со временем, но никогда не откатывается назад
            // относительно изначально запланированного на 13 недель вперёд.
            var targetEnd = new[]
            {
                now.AddDays(horizonWeeks * 7),
                startWeekUtc.AddDays(horizonWeeks * 7)
            }.Max();

            var firstCycle = startWeekUtc;
            // Держим фазу цикла (startWeekUtc + k * cycleDays), не пересчитывая старые циклы.
            while (firstCycle.AddDays(cycleDays) < now.AddDays(-7))
                firstCycle = firstCycle.AddDays(cycleDays);

            return (firstCycle, targetEnd);
        }
    }
}
