namespace AnimatorsWeb.Models
{
    public class AnimJamListViewModel
    {
        public List<AnimJamYearGroup> Years { get; set; } = new();
        public int TotalCount { get; set; }
    }

    public class AnimJamYearGroup
    {
        public int Year { get; set; }
        public List<AnimJamCard> Jams { get; set; } = new();
    }

    public class AnimJamCard
    {
        public int Number { get; set; }
        public string Theme { get; set; } = "";
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int ParticipantsCount { get; set; }
        public int WinnersCount { get; set; }
        public string AnnouncementUrl { get; set; } = "";
        public string ResultsUrl { get; set; } = "";
        public List<string> TopWinners { get; set; } = new();
    }
}