namespace AnimatorsWeb.Models
{
    public class EventPreset
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string DefaultTitle { get; set; } = "";
        public string? DefaultDescription { get; set; }
        public int DurationHours { get; set; } = 2;
        public string? Location { get; set; }
        public bool CreateInDiscord { get; set; } = true;


        public bool IsRecurring { get; set; }
        public int? RecurrenceFrequency { get; set; }   // 0=yearly,1=monthly,2=weekly,3=daily
        public int RecurrenceInterval { get; set; } = 1;
        public string? RecurrenceByWeekdays { get; set; } // CSV: "5" = Friday (0=Mon..6=Sun)

        public TimeSpan? DefaultStartTimeUtc { get; set; }
        public string? Color { get; set; }

    }
}