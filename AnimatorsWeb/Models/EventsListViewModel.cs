namespace AnimatorsWeb.Models
{
    public class EventsListViewModel
    {
        public string Tab { get; set; } = "all";
        public int TotalCount { get; set; }
        public int AniJamCount { get; set; }
        public int ArtJamCount { get; set; }
        public AnimJamStatusBanner? Status { get; set; }
        public List<EventYearGroup> Years { get; set; } = new();
    }

    public class EventYearGroup
    {
        public int Year { get; set; }
        public List<EventCard> Events { get; set; } = new();
    }

    public class EventCard
    {
        public EventType Type { get; set; }
        public int Number { get; set; }
        public string Theme { get; set; } = "";
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int ParticipantsCount { get; set; }
        public int WinnersCount { get; set; }
        public List<string> TopWinners { get; set; } = new();
    }
}