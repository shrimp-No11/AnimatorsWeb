namespace AnimatorsWeb.Models
{
    public class DiscordRecurrenceRule
    {
        public string Start { get; set; } = "";   // ISO 8601 UTC, первое вхождение
        public int Frequency { get; set; }        // 0=yearly, 1=monthly, 2=weekly, 3=daily
        public int Interval { get; set; } = 1;
        public int[]? ByWeekday { get; set; }     // 0=Mon .. 6=Sun
    }
}