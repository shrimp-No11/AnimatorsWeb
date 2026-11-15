namespace AnimatorsWeb.Models
{
    public class EventPattern
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int CycleWeeksCount { get; set; } = 1;
        public List<PatternEvent> Events { get; set; } = new();
        public bool AutoRecurring { get; set; } = true;
    }

    public class PatternEvent
    {
        public int Id { get; set; }
        public int EventPatternId { get; set; }
        public EventPattern? EventPattern { get; set; }
        public int EventPresetId { get; set; }
        public EventPreset? EventPreset { get; set; }
        public int WeekOffset { get; set; }        // 0..CycleWeeksCount-1
        public int DayOfWeek { get; set; }         // 0=Mon .. 6=Sun
        public TimeSpan StartTimeUtc { get; set; } // UTC время суток
    }
}