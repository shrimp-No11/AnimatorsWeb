namespace AnimatorsWeb.Services
{
    /// <summary>Расписание еженедельной синхронизации календаря.</summary>
    public static class CalendarJobSchedule
    {
        /// <summary>02:15 МСК (вторник) = 23:15 UTC (понедельник).</summary>
        public static readonly TimeSpan RunTimeUtc = new(23, 15, 0);

        public static DateTime NextRunUtc(DateTime nowUtc)
        {
            var daysUntilMonday = ((int)DayOfWeek.Monday - (int)nowUtc.DayOfWeek + 7) % 7;
            var candidate = nowUtc.Date.AddDays(daysUntilMonday).Add(RunTimeUtc);
            if (candidate <= nowUtc) candidate = candidate.AddDays(7);
            return candidate;
        }
    }
}
