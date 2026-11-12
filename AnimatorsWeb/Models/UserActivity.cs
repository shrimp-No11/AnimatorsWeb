namespace AnimatorsWeb.Models
{
    public enum ActivityType
    {
        Message = 0,
        VoiceJoin = 1
    }

    public class UserActivity
    {
        public ulong DiscordUserId { get; set; }
        public DateOnly Date { get; set; }         
        public ActivityType Type { get; set; }
    }
}