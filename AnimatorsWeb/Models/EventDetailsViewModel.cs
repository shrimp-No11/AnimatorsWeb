namespace AnimatorsWeb.Models
{
    public class EventDetailsViewModel
    {
        public int Number { get; set; }
        public EventType EventType { get; set; }
        public string Theme { get; set; } = "";
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string? AnnouncementUrl { get; set; }
        public string? ResultsUrl { get; set; }
        public int ParticipantsCount { get; set; }
        public int WinnersCount { get; set; }

        public List<EventWinnerGroup> WinnerGroups { get; set; } = new();
        public List<EventWorkCard> Works { get; set; } = new();

        public string TypeLabel => EventType == EventType.ArtJam ? "ArtJam" : "AniJam";
    }

    public class EventWinnerGroup
    {
        public string NominationDisplayHtml { get; set; } = "";
        public List<EventWinnerPerson> Winners { get; set; } = new();
    }

    public class EventWinnerPerson
    {
        public ulong DiscordUserId { get; set; }
        public string DisplayName { get; set; } = "";
        public string WorkUrl { get; set; } = "";
        public string MessageUrl { get; set; } = "";
    }

    public class EventWorkCard
    {
        public int Id { get; set; }
        public List<EventPerson> People { get; set; } = new();
        public string WorkUrl { get; set; } = "";
        public string MessageUrl { get; set; } = "";
        public string MediaType { get; set; } = "link";
        public string ThumbnailUrl { get; set; } = "";
        public bool IsWinner { get; set; }
        public string BadgeHtml { get; set; } = "";
        public int AttachmentsCount { get; set; } = 1;
    }

    public class EventPerson
    {
        public ulong DiscordUserId { get; set; }
        public string DisplayName { get; set; } = "";
    }
}