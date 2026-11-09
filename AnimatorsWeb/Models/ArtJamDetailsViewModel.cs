namespace AnimatorsWeb.Models
{
    public class ArtJamDetailsViewModel
    {
        public int Number { get; set; }
        public string Theme { get; set; } = "";
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string? AnnouncementUrl { get; set; }
        public string? ResultsUrl { get; set; }
        public int ParticipantsCount { get; set; }
        public List<ArtJamWinnerGroup> WinnerGroups { get; set; } = new();
        public List<ArtJamWorkCard> Works { get; set; } = new();
    }

    public class ArtJamWinnerGroup
    {
        public string Title { get; set; } = "";
        public List<ArtJamPerson> People { get; set; } = new();
    }

    public class ArtJamWorkCard
    {
        public string WorkUrl { get; set; } = "";
        public string MessageUrl { get; set; } = "";
        public int PrimaryParticipantId { get; set; }
        public string MediaType { get; set; } = "link";
        public List<ArtJamPerson> People { get; set; } = new();
    }
}