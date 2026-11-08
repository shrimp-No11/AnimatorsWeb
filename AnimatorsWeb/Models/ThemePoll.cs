namespace AnimatorsWeb.Models
{
    public class ThemePoll
    {
        public EventType EventType { get; set; } = EventType.AniJam;
        public int Id { get; set; }
        public int AnimJamNumber { get; set; }       
        public ulong ChannelId { get; set; }         
        public ulong MessageId { get; set; }      
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}