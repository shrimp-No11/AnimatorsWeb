namespace AnimatorsWeb.Models
{
    public class ChannelSettings
    {
        public int Id { get; set; }

        public ulong? ThemePollChannelId { get; set; }
        public ulong? AnimJamStartChannelId { get; set; }
        public ulong? WinnerPollChannelId { get; set; }      
        public ulong? WinnersAnnouncementChannelId { get; set; } 
        public ulong? AnnouncementChannelId { get; set; }     


        public ulong? ArtJamThemePollChannelId { get; set; }
        public ulong? ArtJamStartChannelId { get; set; }
        public ulong? ArtJamWinnerPollChannelId { get; set; }
        public ulong? ArtJamWinnersAnnouncementChannelId { get; set; }
    }
}