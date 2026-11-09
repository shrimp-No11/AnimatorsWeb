namespace AnimatorsWeb.Models
{
    public class ArtJamWinnersPageViewModel
    {
        public List<AnimJam> AllArtJams { get; set; } = new();
        public int ArtJamNumber { get; set; }
        public string Theme { get; set; } = "";
        public string? AnnouncementUrl { get; set; }
        public ulong PublishChannelId { get; set; }
        public int PollsCount { get; set; }
        public List<ArtJamWinnerItem> Winners { get; set; } = new();
        public string? Message { get; set; }
    }

    public class ArtJamWinnerItem
    {
        public string WorkUrl { get; set; } = "";
        public string MessageUrl { get; set; } = "";
        public int Votes { get; set; }
        public int ParticipantId { get; set; }
        public ulong PrimaryDiscordUserId { get; set; }
        public List<ArtJamPerson> People { get; set; } = new();
        public List<ArtJamAttachment> Attachments { get; set; } = new();
    }

    public class ArtJamAttachment
    {
        public int Index { get; set; }
        public string FileName { get; set; } = "";
        public string Url { get; set; } = "";
        public bool Selected { get; set; } = true;
    }
}