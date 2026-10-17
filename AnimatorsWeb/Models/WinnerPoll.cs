namespace AnimatorsWeb.Models
{
    public class WinnerPoll
    {
        public int Id { get; set; }
        public int AnimJamNumber { get; set; }
        public NominationType Nomination { get; set; }   
        public ulong ChannelId { get; set; }
        public ulong MessageId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string? OptionsJson { get; set; }
    }
}