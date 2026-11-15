namespace AnimatorsWeb.Models
{
    public class CalendarEvent
    {
        public int Id { get; set; }

        public int? EventPresetId { get; set; }
        public EventPreset? EventPreset { get; set; }

        public int? PatternInstanceId { get; set; }
        public PatternInstance? PatternInstance { get; set; }

        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public string? Location { get; set; }
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }

        public ulong? DiscordEventId { get; set; }
        public string Source { get; set; } = "Site";
        public string? DiscordSnapshot { get; set; }
        public DateTime? LastSyncedUtc { get; set; }

        public bool SkipDiscordSync { get; set; }

        public bool IsRecurring { get; set; }
        public string? RecurrenceRuleJson { get; set; }
        public string? RecurrenceExceptionsJson { get; set; }

    }
}