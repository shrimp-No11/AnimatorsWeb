namespace AnimatorsWeb.Models
{
    public class LandingViewModel
    {
        public DesignSettings Design { get; set; } = new();
        public string? GuildIconUrl { get; set; }
        public int ParticipantsCount { get; set; }
        public int AnimJamsCount { get; set; }
        public int WorksCount { get; set; }
        public List<LandingWork> RecentWorks { get; set; } = new();
    }

    public class LandingWork
    {
        public List<LandingAuthor> Authors { get; set; } = new();
        public int ParticipantId { get; set; }
        public int AnimJamNumber { get; set; }
        public string WorkUrl { get; set; } = "";
        public string MessageUrl { get; set; } = "";
        public string ThumbnailUrl { get; set; } = "";
        public string MediaType { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int AttachmentsCount { get; set; } = 1;
    }
    public class LandingAuthor
    {
        public string DisplayName { get; set; } = "";
        public ulong DiscordUserId { get; set; }
    }
}