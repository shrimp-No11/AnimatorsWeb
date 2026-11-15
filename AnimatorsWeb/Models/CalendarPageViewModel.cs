namespace AnimatorsWeb.Models
{
    public class CalendarPageViewModel
    {
        public int Year { get; set; }
        public int Month { get; set; }              // 1..12
        public DateTime FirstOfMonthUtc { get; set; }
        public List<CalendarDayCell> Days { get; set; } = new();

        public bool IsModerator { get; set; }
        public List<EventPreset> Presets { get; set; } = new();
        public List<EventPattern> Patterns { get; set; } = new();
        public List<GuildMemberOption> VoiceChannels { get; set; } = new();
        public List<CalendarEvent> AllGridEvents { get; set; } = new();
    }

    public class CalendarDayCell
    {
        public DateOnly DateMsk { get; set; }
        public bool IsCurrentMonth { get; set; }
        public bool IsToday { get; set; }
        public List<CalendarEvent> Events { get; set; } = new();   // начинающиеся в этот день
    }
}